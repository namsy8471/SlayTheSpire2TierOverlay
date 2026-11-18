using System.Text.Json;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Events;

/// <summary>Apply the full-run event rule to the buttons actually offered. Keep the HP guard stable across UI refreshes.</summary>
public sealed class BaselineEventChoices(IReadOnlyDictionary<string, string>? relicClasses = null)
{
    /// <summary>Relic id (no prefix) → the simulator's option id of an Ancient's relic option, from
    /// <c>model/run-baseline/choice-eligibility.json</c> (<c>relic_classes</c>: class name → relic id). Empty when absent.</summary>
    public static IReadOnlyDictionary<string, string> LoadRelicClasses(string packDirectory)
    {
        var path = Path.Combine(packDirectory, "model", "run-baseline", "choice-eligibility.json");
        if (!File.Exists(path)) return new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var classes = doc.RootElement.GetProperty("relic_classes").Deserialize<Dictionary<string, string>>() ?? [];
        var byRelic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, relic) in classes) byRelic.TryAdd(relic, name);
        return byRelic;
    }

    /// <summary>An option that costs HP is never recommended when it would kill or leave HP below this share of max HP
    /// (the user's rule of 11-18: no paying HP for a benefit when HP is too low). Provisional until the simulator's
    /// measured threshold is relayed.</summary>
    public const double LowHpFloor = 0.3;

    private (string Event, int Floor)? _room;
    private string? _avoid;
    private int _hp;

    public void Reset() { _room = null; _avoid = null; }

    public EventEvaluation Apply(EventEvaluation evaluation, int floor, int hp, string? lastChosen)
        => Apply(evaluation, floor, hp, lastChosen, null, null);

    /// <summary>The rule (first legal button), then the simulator's learned event adjustment when
    /// <paramref name="policy"/> has event weights, then the HP guard: after HP dropped, the button just taken is not
    /// recommended again, whichever layer picked it, and its leave stands (<c>--event-guard-stands</c>). A repeated
    /// button is matched without its count suffix (Slippery Bridge's HOLD_ON_0, HOLD_ON_1 ... HOLD_ON_LOOP).
    /// With <paramref name="maxHp"/> known (above 0), a button whose HP cost would kill or fall under
    /// <see cref="LowHpFloor"/> is never recommended while another button is left.</summary>
    public EventEvaluation Apply(EventEvaluation evaluation, int floor, int hp, string? lastChosen, RunBaselinePolicy? policy, RunState? run,
        int maxHp = 0)
    {
        if (!evaluation.OptionsExact) return evaluation;
        var room = (evaluation.Event.Id, floor);
        if (_room != room) Reset();
        else if (hp < _hp) _avoid = lastChosen is null ? null : Repeat(lastChosen);
        else if (hp > _hp) _avoid = null;
        _room = room;
        _hp = hp;
        var offered = evaluation.Options;
        var affordable = offered.Where(o => !o.IsUnaffordable).ToList();
        var safe = affordable.Where(o => !TooCostly(o, hp, maxHp)).ToList();
        var legal = safe.Count > 0 ? safe : affordable;
        var pick = legal.FirstOrDefault(o => !Excluded(o.Option.Key)) ?? legal.FirstOrDefault();
        var guarded = Avoided(pick);
        var adjusted = false;
        if (!guarded && pick is not null && policy is { HasEventWeights: true } && run is not null
            && Candidates(evaluation, offered, legal) is { } candidates)
        {
            var i = policy.PickEvent(run, evaluation.Event.Id, candidates, Array.IndexOf(offered.ToArray(), pick));
            adjusted = !ReferenceEquals(offered[i], pick);
            pick = offered[i];
            guarded = Avoided(pick);
            if (guarded) adjusted = false;
        }
        if (guarded)
        {
            var alternatives = legal.Where(o => Repeat(o.Option.Key) != _avoid).ToList();
            pick = alternatives.FirstOrDefault(o => o.Option.Leave || o.Option.Key.Contains("EXIT", StringComparison.Ordinal)
                || o.Option.Key.Contains("LEAVE", StringComparison.Ordinal)) ?? alternatives.FirstOrDefault() ?? pick;
        }
        var ordered = offered.OrderByDescending(o => ReferenceEquals(o, pick)).ThenBy(o => o.IsUnaffordable).ToArray();
        return evaluation with
        {
            Options = offered.Select(o => o with { Rank = Array.IndexOf(ordered, o) + 1, IsBest = ReferenceEquals(o, pick) }).ToArray(),
            Confidence = RecommendationConfidence.Normal,
            UsedRunBaseline = true,
            UsedEventAdjust = adjusted,
        };
    }

    /// <summary>The option's own HP cost would kill, or leave HP under <see cref="LowHpFloor"/> of max HP.
    /// False when max HP is unknown (0).</summary>
    public static bool TooCostly(EventOptionEvaluation option, int hp, int maxHp)
        => maxHp > 0 && option.HpCost > 0 && (hp - option.HpCost <= 0 || hp - option.HpCost < LowHpFloor * maxHp);

    private bool Avoided(EventOptionEvaluation? option) => option is not null && _avoid is not null && Repeat(option.Option.Key) == _avoid;

    /// <summary>The button's name without a repeat count: <c>X.pages.HOLD_ON_0.options.HOLD_ON_1</c> and
    /// <c>..._LOOP</c> both read <c>HOLD_ON</c>.</summary>
    internal static string Repeat(string key)
    {
        var name = key[(key.LastIndexOf('.') + 1)..];
        var cut = name.LastIndexOf('_');
        if (cut <= 0) return name;
        var tail = name[(cut + 1)..];
        return tail == "LOOP" || (tail.Length > 0 && tail.All(char.IsAsciiDigit)) ? name[..cut] : name;
    }

    private IReadOnlyList<EventCandidate>? Candidates(EventEvaluation evaluation, IReadOnlyList<EventOptionEvaluation> offered,
        IReadOnlyList<EventOptionEvaluation> legal)
    {
        var ancient = AdoptedEventChoices.IsAncient(evaluation.Event.Id);
        var list = new List<EventCandidate>(offered.Count);
        foreach (var o in offered)
        {
            var id = AdoptedEventChoices.NativeOptionId(evaluation.Event.Id, evaluation.Page, o.Option.ShortName);
            string? relic = null;
            if (ancient)
            {
                var relics = o.Option.Effects.Where(e => e.Type == EventEffectTypes.Relic && e.Relic is not null).ToArray();
                if (relics.Length == 1)
                {
                    relic = relics[0].Relic!.Replace("RELIC.", "", StringComparison.Ordinal);
                    // An Ancient's relic option is named by the relic's class; without the name the keys would not match.
                    if (relicClasses is null || !relicClasses.TryGetValue(relic, out var name)) return null;
                    id = name;
                }
            }
            list.Add(new EventCandidate(id, relic, legal.Contains(o)));
        }
        return list;
    }

    private static bool Excluded(string key) => key.Split('.').Last().ToUpperInvariant() is "SPLASH" or "KALEIDOSCOPE";
}
