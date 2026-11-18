namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// Event pages that show a random few of their options, replayed from the event's own stream
/// (<see cref="RngSeeds.EventStream"/>). The answer maps the option clicked to open a page onto the option keys that page
/// shows, in button order, so the screen's buttons can be lined up with them.
/// <para>
/// Tinker Time (decompiled <c>MegaCrit.Sts2.Core.Models.Events.TinkerTime</c>): <c>ChooseCardTypeAsync</c> offers
/// <c>TakeTwo([Attack, Skill, Power])</c> and <c>ChooseTypeAsync</c> offers <c>TakeTwo</c> of the chosen type's three riders
/// (Attack: Sapping, Violence, Choking; Skill: Energized, Wisdom, Chaos; Power: Expertise, Curious, Improvement).
/// <c>TakeTwo</c> is <c>Rng.Shuffle</c> then the first two. They are the event's only draws, so the riders of each type
/// come from the stream as it stands after the type shuffle.
/// </para>
/// </summary>
public static class EventOfferedOptions
{
    public const string TinkerTimeEventId = "EVENT.TINKER_TIME";

    private const string Tinker = "TINKER_TIME.pages.";

    private static readonly string[] s_types = ["ATTACK", "SKILL", "POWER"];

    private static readonly Dictionary<string, string[]> s_riders = new(StringComparer.Ordinal)
    {
        ["ATTACK"] = ["SAPPING", "VIOLENCE", "CHOKING"],
        ["SKILL"] = ["ENERGIZED", "WISDOM", "CHAOS"],
        ["POWER"] = ["EXPERTISE", "CURIOUS", "IMPROVEMENT"],
    };

    /// <summary>True for an event with a page this class can replay.</summary>
    public static bool Handles(string eventId) => eventId == TinkerTimeEventId;

    /// <summary>
    /// Option key clicked → the option keys of the page it opens; null for an event this class does not handle.
    /// <paramref name="childShuffle"/>: the v0.113 shuffle (one parent draw seeds a child stream).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? For(string eventId, ulong eventSeed, bool childShuffle)
    {
        if (eventId != TinkerTimeEventId) return null;
        var types = TakeTwo(new GameRng(eventSeed), s_types, childShuffle);
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Tinker + "INITIAL.options.CHOOSE_CARD_TYPE"] = types.Select(t => Tinker + "CHOOSE_CARD_TYPE.options." + t).ToArray(),
        };
        foreach (var t in types)
        {
            var rng = new GameRng(eventSeed);
            TakeTwo(rng, s_types, childShuffle); // the type page's draws come first
            var riders = TakeTwo(rng, s_riders[t], childShuffle);
            map[Tinker + "CHOOSE_CARD_TYPE.options." + t] = riders.Select(r => Tinker + "CHOOSE_RIDER.options." + r).ToArray();
        }
        return map;
    }

    private static string[] TakeTwo(GameRng rng, string[] candidates, bool childShuffle)
    {
        var list = candidates.ToList();
        rng.Shuffle(list, childShuffle);
        return list.Take(2).ToArray();
    }
}
