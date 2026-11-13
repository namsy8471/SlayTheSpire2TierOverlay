using System.Text.Json;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Events;

/// <summary>
/// A0 first-opportunity deployment. Missed screens cannot be reconstructed from a later save;
/// those runs keep the baseline. Cached choices survive refreshes and language changes.
/// </summary>
public sealed class AdoptedEventChoices
{
    private readonly PairedChoicePolicy _ancient, _event;
    private readonly HashSet<string> _ordinary;
    private readonly Dictionary<string, string> _relicClasses;
    private readonly Dictionary<string, (int Floor, string Event, string Page, string Pick, string? LastChoice)> _used = [];
    private string? _runKey;
    private bool _observedFromAct1;
    private (int Floor, string Event, string Kind)? _pending;
    private bool _resolved;
    private int _floor;
    private bool _earlierPageIneligible;

    private AdoptedEventChoices(string directory)
    {
        _ancient = PairedChoicePolicy.Load(File.ReadAllText(Path.Combine(directory, "ancient-policy.json")));
        _event = PairedChoicePolicy.Load(File.ReadAllText(Path.Combine(directory, "event-policy.json")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "choice-eligibility.json")));
        _ordinary = doc.RootElement.GetProperty("ordinary_events").Deserialize<HashSet<string>>()!;
        _relicClasses = doc.RootElement.GetProperty("relic_classes").Deserialize<Dictionary<string, string>>()!;
        if (_ordinary is null || _relicClasses is null) throw new InvalidDataException("Missing choice eligibility metadata.");
    }

    public static AdoptedEventChoices? LoadDirectory(string packDirectory)
    {
        var directory = Path.Combine(packDirectory, "model", "run-baseline");
        return File.Exists(Path.Combine(directory, "ancient-policy.json")) && File.Exists(Path.Combine(directory, "event-policy.json"))
            ? new(directory) : null;
    }

    public void Observe(string? runKey, RunState? run, string? eventId)
    {
        if (runKey != _runKey || run is null || run.Floor < _floor)
        {
            _used.Clear(); _pending = null; _resolved = false; _earlierPageIneligible = false;
            _runKey = runKey;
            _observedFromAct1 = runKey is not null && run?.Act == 1;
        }
        if (run is null) return;
        _floor = run.Floor;
        if (_pending is { } pending && (pending.Floor != run.Floor || pending.Event != eventId))
        {
            // An unseen screen may have been the first eligible opportunity: do not move it to a later room.
            if (!_resolved) _used.TryAdd(pending.Kind, (pending.Floor, pending.Event, "", "", null));
            _pending = null;
        }
        if (eventId is null || run.Act is not (2 or 3)) return;
        var kind = IsAncient(eventId) ? "ancient" : "event";
        if ((kind == "ancient" && run.Act != 2) || !EligibleEvent(eventId, kind)) return;
        if (_pending is null) { _pending = (run.Floor, eventId, kind); _resolved = false; _earlierPageIneligible = false; }
    }

    public EventEvaluation Apply(EventEvaluation baseline, RunState run, bool stateExact, string? lastChosen = null)
    {
        if (!_observedFromAct1 || !RunBaselinePolicy.Supports(run) || run.Ascension != 0 || run.Act is not (2 or 3)) return baseline;
        var kind = baseline.Event.IsAncient ? "ancient" : "event";
        if ((kind == "ancient" && run.Act != 2) || !EligibleEvent(baseline.Event.Id, kind)) return baseline;
        if (_used.TryGetValue(kind, out var previous))
            return previous.Floor == run.Floor && previous.Event == baseline.Event.Id && previous.Page == baseline.Page && previous.LastChoice == lastChosen
                ? SetPick(baseline, previous.Pick) : baseline;
        if (!baseline.OptionsExact) return baseline;
        // The same visible screen can gain a fresh save a moment later. Defer until it is exact;
        // leaving it unresolved consumes the opportunity in Observe, rather than shifting it later.
        if (!stateExact || run.ActBoss is null) { _resolved = false; return baseline; }
        if ((lastChosen is not null || baseline.Page != baseline.Event.InitialPage?.Page) && !_earlierPageIneligible)
        {
            _used[kind] = (run.Floor, baseline.Event.Id, "", "", null);
            return baseline;
        }
        _resolved = true;
        var candidates = new List<VisibleEventCandidate>();
        foreach (var option in baseline.Options)
        {
            var content = "";
            var id = NativeOptionId(baseline.Event.Id, baseline.Page, option.Option.ShortName);
            if (kind == "ancient")
            {
                var relics = option.Option.Effects.Where(e => e.Type == EventEffectTypes.Relic && e.Relic is not null).ToArray();
                if (relics.Length != 1) return baseline;
                content = relics[0].Relic!.Replace("RELIC.", "", StringComparison.Ordinal);
                id = _relicClasses.FirstOrDefault(pair => pair.Value == content).Key;
                if (id is null) return baseline;
            }
            candidates.Add(new(id, content, !option.IsUnaffordable));
        }
        var rule = baseline.Options.ToList().FindIndex(o => o.IsBest);
        var legal = Enumerable.Range(0, candidates.Count).Where(i => candidates[i].Legal).ToArray();
        if (legal.Length < 2) { _earlierPageIneligible = true; return baseline; }
        if (rule < 0 || !candidates[rule].Legal || legal.Select(i => candidates[i].Id).Distinct().Count() != legal.Length) return baseline;
        // Larger menus require the research hash cap's full visible-state tuple. Missing context keeps the rule.
        if (legal.Length > 3)
        {
            _used[kind] = (run.Floor, baseline.Event.Id, "", "", null);
            return baseline;
        }
        var pick = (kind == "ancient" ? _ancient : _event).Pick(run, baseline.Event.Id, candidates, rule, legal.Where(i => i != rule).ToArray());
        var key = baseline.Options[pick].Option.Key;
        _used[kind] = (run.Floor, baseline.Event.Id, baseline.Page, key, lastChosen);
        return SetPick(baseline, key);
    }

    internal static string NativeOptionId(string eventId, string page, string shortName) =>
        eventId == "EVENT.COLOSSAL_FLOWER" && page == "INITIAL" ? shortName switch
        {
            "EXTRACT_CURRENT_PRIZE_1" => "EXTRACT_CURRENT_PRIZE",
            "REACH_DEEPER_1" => "REACH_DEEPER",
            _ => shortName,
        } : shortName;

    private static EventEvaluation SetPick(EventEvaluation evaluation, string key)
    {
        var pick = evaluation.Options.FirstOrDefault(o => o.Option.Key == key);
        if (pick is null || pick.IsUnaffordable) return evaluation;
        var ordered = evaluation.Options.OrderByDescending(o => ReferenceEquals(o, pick)).ThenBy(o => o.Rank).ToArray();
        return evaluation with
        {
            Options = evaluation.Options.Select(o => o with { IsBest = ReferenceEquals(o, pick), Rank = Array.IndexOf(ordered, o) + 1 }).ToArray(),
            UsedAdoptedPolicy = true,
        };
    }

    private bool EligibleEvent(string eventId, string kind) => kind == "ancient" ? IsAncient(eventId) && eventId != "EVENT.NEOW"
        : _ordinary.Contains(eventId) && !IsAncient(eventId) && eventId is not ("EVENT.RELIC_TRADER" or "EVENT.THE_FUTURE_OF_POTIONS");
    private static bool IsAncient(string id) => id is "EVENT.DARV" or "EVENT.NEOW" or "EVENT.NONUPEIPE" or "EVENT.OROBAS"
        or "EVENT.PAEL" or "EVENT.TANX" or "EVENT.TEZCATARA" or "EVENT.VAKUU";
}
