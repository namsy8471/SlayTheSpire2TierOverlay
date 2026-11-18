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

    private (string Event, int Floor)? _room;
    private string? _avoid;
    private int _hp;

    public void Reset() { _room = null; _avoid = null; }

    public EventEvaluation Apply(EventEvaluation evaluation, int floor, int hp, string? lastChosen)
        => Apply(evaluation, floor, hp, lastChosen, null, null);

    /// <summary>The rule (first legal button, HP guard), then the simulator's learned event adjustment when
    /// <paramref name="policy"/> has event weights. A guard leave stands over the adjustment (<c>--event-guard-stands</c>).</summary>
    public EventEvaluation Apply(EventEvaluation evaluation, int floor, int hp, string? lastChosen, RunBaselinePolicy? policy, RunState? run)
    {
        if (!evaluation.OptionsExact) return evaluation;
        var room = (evaluation.Event.Id, floor);
        if (_room != room) Reset();
        else if (hp < _hp) _avoid = lastChosen;
        else if (hp > _hp) _avoid = null;
        var offered = evaluation.Options;
        var legal = offered.Where(o => !o.IsUnaffordable).ToList();
        var pick = legal.FirstOrDefault(o => !Excluded(o.Option.Key)) ?? legal.FirstOrDefault();
        var guarded = false;
        if (pick is not null && pick.Option.Key == _avoid)
        {
            guarded = true;
            var alternatives = legal.Where(o => o.Option.Key != _avoid).ToList();
            pick = alternatives.FirstOrDefault(o => o.Option.Leave || o.Option.Key.Contains("EXIT", StringComparison.Ordinal)
                || o.Option.Key.Contains("LEAVE", StringComparison.Ordinal)) ?? alternatives.FirstOrDefault() ?? pick;
        }
        _room = room;
        _hp = hp;
        var adjusted = false;
        if (!guarded && pick is not null && policy is { HasEventWeights: true } && run is not null
            && Candidates(evaluation, offered) is { } candidates)
        {
            var i = policy.PickEvent(run, evaluation.Event.Id, candidates, Array.IndexOf(offered.ToArray(), pick));
            adjusted = !ReferenceEquals(offered[i], pick);
            pick = offered[i];
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

    private IReadOnlyList<EventCandidate>? Candidates(EventEvaluation evaluation, IReadOnlyList<EventOptionEvaluation> offered)
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
            list.Add(new EventCandidate(id, relic, !o.IsUnaffordable));
        }
        return list;
    }

    private static bool Excluded(string key) => key.Split('.').Last().ToUpperInvariant() is "SPLASH" or "KALEIDOSCOPE";
}
