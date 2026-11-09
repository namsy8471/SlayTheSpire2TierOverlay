using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Overlay.Core.Learning;

/// <summary>
/// One offered card as the learned model reads it: canonical id, upgrade level, copies already in the deck, the engine's
/// score components by key (summed per key, as <see cref="Scoring.ScoreBreakdown.ValueOf"/> gives them) and the pack's
/// data-fit <c>baseAdjust</c> inside the <c>base</c> component, which the model takes out.
/// </summary>
public sealed record PolicyOption(string Card, int Upgrade, int Copies, IReadOnlyDictionary<string, double> Components, double BaseAdjust);

/// <summary>
/// The learned card pick model (docs/research/LEARNED_CARD_POLICY_PLAN.md 5.4), trained offline by
/// <c>tools/CardPolicy/export_model.py</c> on top players' reward screens and shipped as <c>model/card-policy.json</c>.
/// One softmax per screen over {Skip, offered cards}:
/// <code>
/// u(card) = scale·Σ w_k·comp_k + w_up·upgraded + w_copies·min(copies, cap) + value[card] + act[card][a] + a[card]·mean(b over deck)
/// u(skip) = scale·Σ v_k·skipComp_k + skipAct[a] + skipA·mean(b over deck)
/// </code>
/// A card the model never saw gets no value/act/a terms (its engine components still count).
/// </summary>
public sealed class CardPolicyModel
{
    /// <summary>Offers a card needs in the training data before its learned value is trusted over the engine's rating.</summary>
    public const int MinOffers = 30;

    private readonly ModelFile _f;
    private readonly int _baseIndex;

    private CardPolicyModel(ModelFile f)
    {
        _f = f;
        _baseIndex = Array.IndexOf(f.CardComponents, "base");
        if (_baseIndex < 0 || f.CardWeights.Length != f.CardComponents.Length || f.SkipWeights.Length != f.SkipComponents.Length
            || f.SkipAct.Length != 3 || f.SkipA.Length != f.Dim || f.Cards.Values.Any(c => c.A.Length != f.Dim || c.B.Length != f.Dim || c.Act.Length != 3))
        {
            throw new InvalidDataException("card-policy.json: inconsistent sizes.");
        }

        if (BaseWeight <= 0)
        {
            throw new InvalidDataException("card-policy.json: the base component weight must be positive (it sets the point scale).");
        }
    }

    /// <summary>Utility of one tier-base point: dividing a utility by this gives "engine base points" for display.</summary>
    public double PointUtility => _f.Scale * BaseWeight;

    private double BaseWeight => _f.CardWeights[_baseIndex];

    public static CardPolicyModel Load(string json)
    {
        var f = JsonSerializer.Deserialize<ModelFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("card-policy.json is empty.");
        return new CardPolicyModel(f);
    }

    /// <summary>True when the model has seen the card offered at least <see cref="MinOffers"/> times.</summary>
    public bool Knows(string cardId) => _f.Cards.TryGetValue(cardId, out var c) && c.N >= MinOffers;

    /// <summary>
    /// Utility parts per option, Skip first: <c>result[0]</c> is Skip, <c>result[j]</c> is <paramref name="options"/>[j − 1].
    /// Each part is (key, utility); the utility of an option is the sum of its parts.
    /// </summary>
    /// <remarks><paramref name="act"/> is 1-based (clamped to 1..3); <paramref name="deck"/> holds the canonical card ids in the
    /// deck right before the screen (upgrades and enchantments are not part of the deck summary).</remarks>
    public IReadOnlyList<IReadOnlyList<(string Key, double Utility)>> Parts(
        int act, IReadOnlyList<string> deck, IReadOnlyList<PolicyOption> options, IReadOnlyDictionary<string, double> skipComponents)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(skipComponents);
        var a = Math.Clamp(act, 1, 3) - 1;

        // Deck summary: the mean of the deck cards' b vectors (unknown cards count as zero vectors, as in training).
        var d = new double[_f.Dim];
        if (deck.Count > 0)
        {
            foreach (var id in deck)
            {
                if (_f.Cards.TryGetValue(id, out var c))
                {
                    for (var k = 0; k < d.Length; k++) d[k] += c.B[k];
                }
            }

            for (var k = 0; k < d.Length; k++) d[k] /= deck.Count;
        }

        var result = new List<IReadOnlyList<(string, double)>>(options.Count + 1);
        var skip = new List<(string, double)>();
        for (var k = 0; k < _f.SkipComponents.Length; k++)
        {
            skip.Add((_f.SkipComponents[k], _f.Scale * _f.SkipWeights[k] * skipComponents.GetValueOrDefault(_f.SkipComponents[k])));
        }

        skip.Add((PolicyKeys.SkipAct, _f.SkipAct[a]));
        skip.Add((PolicyKeys.DeckFit, Dot(_f.SkipA, d)));
        result.Add(skip);

        foreach (var o in options)
        {
            var parts = new List<(string, double)>();
            for (var k = 0; k < _f.CardComponents.Length; k++)
            {
                var key = _f.CardComponents[k];
                var value = o.Components.GetValueOrDefault(key);
                if (k == _baseIndex) value -= o.BaseAdjust;
                parts.Add((key, _f.Scale * _f.CardWeights[k] * value));
            }

            parts.Add((PolicyKeys.Upgraded, o.Upgrade > 0 ? _f.UpgradedWeight : 0.0));
            parts.Add((PolicyKeys.Copies, _f.CopiesWeight * Math.Min(o.Copies, _f.CopiesCap)));
            if (_f.Cards.TryGetValue(o.Card, out var c))
            {
                parts.Add((PolicyKeys.CardValue, c.Value + c.Act[a]));
                parts.Add((PolicyKeys.DeckFit, Dot(c.A, d)));
            }
            else
            {
                parts.Add((PolicyKeys.CardValue, 0.0));
                parts.Add((PolicyKeys.DeckFit, 0.0));
            }

            result.Add(parts);
        }

        return result;
    }

    /// <summary>Utilities only, Skip first (see <see cref="Parts"/>).</summary>
    public double[] Utilities(int act, IReadOnlyList<string> deck, IReadOnlyList<PolicyOption> options, IReadOnlyDictionary<string, double> skipComponents)
        => Parts(act, deck, options, skipComponents).Select(p => p.Sum(x => x.Utility)).ToArray();

    /// <summary>Engine component weight relative to the base weight (1 for base itself, 0 for a key the model does not weigh).</summary>
    public double RelativeWeight(string componentKey)
    {
        var i = Array.IndexOf(_f.CardComponents, componentKey);
        if (i >= 0) return _f.CardWeights[i] / BaseWeight;
        i = Array.IndexOf(_f.SkipComponents, componentKey);
        return i >= 0 ? _f.SkipWeights[i] / BaseWeight : 0.0;
    }

    private static double Dot(double[] x, double[] y)
    {
        var s = 0.0;
        for (var i = 0; i < x.Length; i++) s += x[i] * y[i];
        return s;
    }

    private sealed class ModelFile
    {
        public int Version { get; set; }
        public double Scale { get; set; }
        public int CopiesCap { get; set; } = 3;
        public string[] CardComponents { get; set; } = [];
        public double[] CardWeights { get; set; } = [];
        public double UpgradedWeight { get; set; }
        public double CopiesWeight { get; set; }
        public string[] SkipComponents { get; set; } = [];
        public double[] SkipWeights { get; set; } = [];
        public double[] SkipAct { get; set; } = [];
        public int Dim { get; set; }

        [JsonPropertyName("skipA")]
        public double[] SkipA { get; set; } = [];

        public Dictionary<string, CardEntry> Cards { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CardEntry
    {
        public int N { get; set; }
        public double Value { get; set; }
        public double[] Act { get; set; } = [];
        public double[] A { get; set; } = [];
        public double[] B { get; set; } = [];
    }
}

/// <summary>Score component keys the learned model adds (next to the engine's <see cref="Scoring.ComponentKeys"/>).</summary>
public static class PolicyKeys
{
    /// <summary>How much top players like the card itself, beyond the engine's components ("랭커 선호").</summary>
    public const string CardValue = "ranker_value";

    /// <summary>How well the card goes with the cards already in the deck, learned from top players ("덱 궁합").</summary>
    public const string DeckFit = "deck_fit";

    public const string Upgraded = "upgraded";
    public const string Copies = "copies";

    /// <summary>Per-act skip tendency of top players.</summary>
    public const string SkipAct = "skip_act";
}
