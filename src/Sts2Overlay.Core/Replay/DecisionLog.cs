using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Overlay.Core.History;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;
using Sts2Overlay.Core.Shop;

namespace Sts2Overlay.Core.Replay;

/// <summary>
/// One line of the play-time decision log (<c>%LOCALAPPDATA%\Sts2Overlay\decisions\&lt;runkey&gt;.jsonl</c>): what the
/// overlay recommended on one card screen at the moment the screen closed. It is a record of the OVERLAY's advice, written
/// so the run review can judge the player against the advice they actually saw instead of what today's engine would say.
/// Nothing here is ever read by a scorer: the player's picks never feed back into any recommendation.
/// Ids only: no Steam id, no path, no deck.
/// </summary>
public sealed record DecisionRecord
{
    public const string CardKind = "card";
    public const string ShopKind = "shop";
    public const string SkipPick = "skip";

    /// <summary>Format version.</summary>
    public int V { get; init; } = 1;

    /// <summary><see cref="CardKind"/> (reward / choose-a-card / event grid) or <see cref="ShopKind"/>.</summary>
    public string Kind { get; init; } = CardKind;

    /// <summary>Run key (<see cref="DecisionLog.RunKey(string?, string?, int)"/>), also the file name.</summary>
    public string Run { get; init; } = "";

    public string Seed { get; init; } = "";
    public string Character { get; init; } = "";
    public int Ascension { get; init; }
    public int Act { get; init; }

    /// <summary>Global floor (same numbering as the <c>.run</c> file's map points).</summary>
    public int Floor { get; init; }

    /// <summary>Screen kind as recognised: <c>reward</c>, <c>choice</c>, <c>grid</c>, <c>shop</c>.</summary>
    public string Screen { get; init; } = "";

    /// <summary><see cref="OfferContext"/> name the cards were scored in (CardReward / EliteReward / BossReward / Event / Shop).</summary>
    public string Context { get; init; } = "";

    /// <summary>Offered cards in slot order, <c>CARD.X</c> or <c>CARD.X+1</c>.</summary>
    public IReadOnlyList<string> Offered { get; init; } = [];

    /// <summary>Card screens: the recommended card (<c>CARD.X+1</c>), <see cref="SkipPick"/>, or null = no verdict (see <see cref="Why"/>).</summary>
    public string? Pick { get; init; }

    /// <summary>Why there is no verdict: <c>noData</c> (no rating for any offered card) or <c>partial</c> (a card was not read).</summary>
    public string? Why { get; init; }

    /// <summary><see cref="RecommendationConfidence"/> name (card screens).</summary>
    public string? Confidence { get; init; }

    /// <summary>Card totals in slot order, rounded to 0.1 (card screens).</summary>
    public IReadOnlyList<double>? Scores { get; init; }

    /// <summary>Skip total, rounded to 0.1 (card screens).</summary>
    public double? Skip { get; init; }

    /// <summary>Shop: gold the bundle was planned with, null when unknown.</summary>
    public int? Gold { get; init; }

    /// <summary>Shop: the recommended purchase (empty = buy nothing).</summary>
    public IReadOnlyList<ShopBuyRecord>? Buy { get; init; }

    /// <summary>Which card model produced the advice: <c>learned</c> or <c>engine</c>.</summary>
    public string Model { get; init; } = "";

    /// <summary>App version.</summary>
    public string App { get; init; } = "";

    /// <summary>Data pack folder name (e.g. <c>v0.107.1</c>).</summary>
    public string Pack { get; init; } = "";

    public DateTime Utc { get; init; }

    /// <summary>The stored recommendation as a card, null for skip / no verdict.</summary>
    [JsonIgnore]
    public CardRef? PickCard => Pick is null or SkipPick ? null : DecisionLog.ParseCard(Pick);

    /// <summary>Whether the record carries a verdict (a card or skip).</summary>
    [JsonIgnore]
    public bool HasVerdict => Pick is not null;
}

/// <summary>One item of a shop's recommended bundle.</summary>
/// <param name="Kind"><c>card</c>, <c>relic</c> or <c>removal</c>.</param>
/// <param name="Id">Card (<c>CARD.X+1</c>), relic id, or for removal the card to remove (may be null).</param>
/// <param name="Price">Price in gold (an estimate when <paramref name="Estimated"/>).</param>
/// <param name="Estimated">The price label was not read.</param>
public sealed record ShopBuyRecord(string Kind, string? Id, int Price, bool Estimated = false);

/// <summary>Shared header of the records written for one evaluation.</summary>
public sealed record DecisionHeader(string Seed, string Character, int Ascension, int Act, int Floor, string Model, string App, string Pack, DateTime Utc);

/// <summary>Pure helpers of the decision log: run key, record construction, (de)serialization.</summary>
public static class DecisionLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// <c>&lt;seed&gt;_&lt;CHARACTER&gt;_a&lt;ascension&gt;</c>, e.g. <c>NTNV7N8F97_IRONCLAD_a5</c>, safe as a file name. The game
    /// writes the same seed into the finished <c>.run</c> file, so the review can find the log of a run. Null without a seed.
    /// </summary>
    public static string? RunKey(string? seed, string? character, int ascension)
    {
        if (string.IsNullOrWhiteSpace(seed)) return null;
        var ch = (character ?? "").Replace("CHARACTER.", "", StringComparison.Ordinal);
        return Safe(seed) + "_" + (ch.Length > 0 ? Safe(ch) : "X") + "_a" + ascension.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The key of a finished run for one of its players.</summary>
    public static string? RunKey(RunHistory run, RunPlayer player)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(player);
        return RunKey(run.Seed, player.Character, run.Ascension);
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim()) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' ? c : '-');
        return sb.ToString();
    }

    /// <summary><c>CARD.X</c> / <c>CARD.X+1</c>.</summary>
    public static string CardText(CardRef card) => card.ToString();

    /// <summary>Inverse of <see cref="CardText"/>; a malformed upgrade suffix is read as part of the id.</summary>
    public static CardRef ParseCard(string text)
    {
        var plus = text.LastIndexOf('+');
        return plus > 0 && int.TryParse(text.AsSpan(plus + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var up)
            ? new CardRef(text[..plus], up)
            : new CardRef(text);
    }

    private static double R(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Record of a card reward / choose-a-card / event card screen. <paramref name="partial"/>: some slot was not read when
    /// the verdict was shown, so the record carries no verdict (<c>why = partial</c>).
    /// </summary>
    public static DecisionRecord ForOffer(DecisionHeader h, string screen, OfferContext context, IReadOnlyList<CardRef> offered, OfferEvaluation eval, bool partial)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(eval);
        string? pick, why = null;
        if (eval.NoData) (pick, why) = (null, "noData");
        else if (partial) (pick, why) = (null, "partial");
        else if (eval.SkipRecommended) pick = DecisionRecord.SkipPick;
        else if (eval.Best is { } best) pick = CardText(best);
        else (pick, why) = (null, "noData");

        return Base(h) with
        {
            Kind = DecisionRecord.CardKind,
            Screen = screen,
            Context = context.ToString(),
            Offered = offered.Select(CardText).ToArray(),
            Pick = pick,
            Why = why,
            Confidence = eval.Confidence.ToString(),
            Scores = eval.Cards.Select(c => R(c.Total)).ToArray(),
            Skip = R(eval.Skip.Total),
        };
    }

    /// <summary>Record of a merchant screen: the cards on screen and the recommended bundle. Not used by the review (yet).</summary>
    public static DecisionRecord ForShop(DecisionHeader h, IReadOnlyList<CardRef> offered, ShopEvaluation eval)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(eval);
        return Base(h) with
        {
            Kind = DecisionRecord.ShopKind,
            Screen = "shop",
            Context = nameof(OfferContext.Shop),
            Offered = offered.Select(CardText).ToArray(),
            Gold = eval.GoldKnown ? eval.Gold : null,
            Buy = eval.RecommendedPurchase.Select(i => i.Kind switch
            {
                ShopItemKind.Card => new ShopBuyRecord("card", i.Card is { } c ? CardText(c) : null, i.Price, i.PriceEstimated),
                ShopItemKind.Relic => new ShopBuyRecord("relic", i.RelicId, i.Price, i.PriceEstimated),
                ShopItemKind.Potion => new ShopBuyRecord("potion", i.PotionId, i.Price, i.PriceEstimated),
                _ => new ShopBuyRecord("removal", i.RemovalTarget, i.Price, i.PriceEstimated),
            }).ToArray(),
        };
    }

    private static DecisionRecord Base(DecisionHeader h) => new()
    {
        Run = RunKey(h.Seed, h.Character, h.Ascension) ?? "",
        Seed = h.Seed,
        Character = h.Character,
        Ascension = h.Ascension,
        Act = h.Act,
        Floor = h.Floor,
        Model = h.Model,
        App = h.App,
        Pack = h.Pack,
        Utc = h.Utc,
    };

    /// <summary>One JSON line (no newline).</summary>
    public static string Serialize(DecisionRecord record) => JsonSerializer.Serialize(record, Json);

    /// <summary>
    /// Reads JSON lines. A line that does not parse (the app crashed mid-write, or a newer format) is skipped, so a broken
    /// last line never hides the records before it.
    /// </summary>
    public static IReadOnlyList<DecisionRecord> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<DecisionRecord>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<DecisionRecord>(line, Json) is { Run.Length: > 0 } r) result.Add(r);
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

    /// <summary>A short log line: <c>act 1 floor 3 reward → CARD.X (learned)</c>. Ids only.</summary>
    public static string Describe(DecisionRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var what = r.Kind == DecisionRecord.ShopKind
            ? r.Buy is { Count: > 0 } buy ? "buy " + string.Join(", ", buy.Select(b => b.Kind == "removal" ? "removal" : b.Id ?? b.Kind)) : "buy nothing"
            : r.Pick ?? $"no verdict ({r.Why})";
        return string.Create(CultureInfo.InvariantCulture, $"act {r.Act} floor {r.Floor} {r.Screen} → {what} ({r.Model})");
    }
}

/// <summary>
/// Finds the play-time record of each card decision of a finished run.
/// <list type="bullet">
/// <item>Only <see cref="DecisionRecord.CardKind"/> records of the same run key, written no earlier than
/// <see cref="ClockSlack"/> before the run's start time (a re-played seed starts a new run later).</item>
/// <item>Same offered card multiset by id (aliases resolved; upgrades are not compared because the overlay reads them
/// from the title colour). A record that read 2 of 3 cards, or any <c>partial</c> record, never matches: that decision
/// falls back to recomputation.</item>
/// <item>Same global floor first; then a neighbouring floor (±<see cref="FloorTolerance"/>) for the decisions left, in
/// case the tracker's floor was one step behind or ahead when the screen was open.</item>
/// <item>Each record is used at most once, in log order (two reward screens of one floor match their own records).</item>
/// </list>
/// </summary>
public static class DecisionLogMatcher
{
    public const int FloorTolerance = 1;
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(10);

    /// <summary>The records that belong to this run of this player.</summary>
    public static IReadOnlyList<DecisionRecord> ForRun(RunHistory run, RunPlayer player, IEnumerable<DecisionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var key = DecisionLog.RunKey(run, player);
        if (key is null) return [];
        var from = run.StartTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(run.StartTime).UtcDateTime - ClockSlack : DateTime.MinValue;
        return records.Where(r => r.Run == key && r.Utc >= from).ToArray();
    }

    /// <summary>Per decision (same order), its record or null. Shop decisions never match.</summary>
    public static IReadOnlyList<DecisionRecord?> Match(IReadOnlyList<CardDecision> decisions, IReadOnlyList<DecisionRecord> records, Func<string, string>? canonical = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(records);
        canonical ??= id => id;
        var result = new DecisionRecord?[decisions.Count];
        var candidates = records.Where(r => r.Kind == DecisionRecord.CardKind && r.Why != "partial").ToList();
        var used = new bool[candidates.Count];
        var recordKeys = candidates.Select(r => Key(r.Offered.Select(o => DecisionLog.ParseCard(o).Id), canonical)).ToArray();
        var decisionKeys = decisions.Select(d => Key(d.Offered.Select(c => c.Id), canonical)).ToArray();

        for (var pass = 0; pass <= FloorTolerance; pass++)
        {
            for (var i = 0; i < decisions.Count; i++)
            {
                var d = decisions[i];
                if (result[i] is not null || d.Context == OfferContext.Shop || d.Offered.Count == 0) continue;
                for (var j = 0; j < candidates.Count; j++)
                {
                    if (used[j] || Math.Abs(candidates[j].Floor - d.Node.Floor) != pass || recordKeys[j] != decisionKeys[i]) continue;
                    used[j] = true;
                    result[i] = candidates[j];
                    break;
                }
            }
        }
        return result;
    }

    private static string Key(IEnumerable<string> ids, Func<string, string> canonical)
        => string.Join(",", ids.Select(canonical).Order(StringComparer.Ordinal));
}
