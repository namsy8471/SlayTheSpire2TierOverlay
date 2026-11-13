using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Events;

/// <summary>Apply the full-run event rule to the buttons actually offered. Keep the HP guard stable across UI refreshes.</summary>
public sealed class BaselineEventChoices
{
    private (string Event, int Floor)? _room;
    private string? _avoid;
    private int _hp;

    public void Reset() { _room = null; _avoid = null; }

    public EventEvaluation Apply(EventEvaluation evaluation, int floor, int hp, string? lastChosen)
    {
        if (!evaluation.OptionsExact) return evaluation;
        var room = (evaluation.Event.Id, floor);
        if (_room != room) Reset();
        else if (hp < _hp) _avoid = lastChosen;
        else if (hp > _hp) _avoid = null;
        var offered = evaluation.Options;
        var legal = offered.Where(o => !o.IsUnaffordable).ToList();
        var pick = legal.FirstOrDefault(o => !Excluded(o.Option.Key)) ?? legal.FirstOrDefault();
        if (pick is not null && pick.Option.Key == _avoid)
        {
            var alternatives = legal.Where(o => o.Option.Key != _avoid).ToList();
            pick = alternatives.FirstOrDefault(o => o.Option.Leave || o.Option.Key.Contains("EXIT", StringComparison.Ordinal)
                || o.Option.Key.Contains("LEAVE", StringComparison.Ordinal)) ?? alternatives.FirstOrDefault() ?? pick;
        }
        _room = room;
        _hp = hp;
        var ordered = offered.OrderByDescending(o => ReferenceEquals(o, pick)).ThenBy(o => o.IsUnaffordable).ToArray();
        return evaluation with
        {
            Options = offered.Select(o => o with { Rank = Array.IndexOf(ordered, o) + 1, IsBest = ReferenceEquals(o, pick) }).ToArray(),
            Confidence = RecommendationConfidence.Normal,
            UsedRunBaseline = true,
        };
    }

    private static bool Excluded(string key) => key.Split('.').Last().ToUpperInvariant() is "SPLASH" or "KALEIDOSCOPE";
}
