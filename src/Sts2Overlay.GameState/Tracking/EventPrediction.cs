using Sts2Overlay.Core.Map;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Save;

namespace Sts2Overlay.GameState.Tracking;

/// <summary>Result of <see cref="EventPrediction.Predict"/>.</summary>
/// <param name="EventId">The event the game pulls.</param>
/// <param name="Certain">False when an approximate <c>IsAllowed</c> check decided between this event and another.</param>
/// <param name="Alternatives">Events the pull gives instead if an approximate check went the other way (list order).</param>
public sealed record EventGuess(string EventId, bool Certain, IReadOnlyList<string> Alternatives);

/// <summary>
/// Which event a single-player event room holds, from the save the game writes when the player steps on the map node.
/// <para>
/// The game writes <c>current_run.save</c> in <c>RunManager.EnterMapPointInternal</c> BEFORE it rolls the "?" room type
/// and BEFORE it pulls the event (RunManager.cs:826 → :840-842). The pull (<c>ActModel.PullNextEvent</c> →
/// <c>RoomSet.EnsureNextEventIsValid</c>) only reads what that save holds:
/// </para>
/// <code>
/// i = events_visited
/// repeat len(event_ids) times:
///     e = event_ids[i % n]
///     if e.IsAllowed(state) and e not in events_seen: break
///     i += 1
/// </code>
/// An ancient node holds <c>rooms.ancient_id</c> instead. The room type itself needs no prediction: the log line
/// "Preloading 'Event Room'" already says the "?" rolled an event.
/// </summary>
public static class EventPrediction
{
    /// <summary>The Lantern Key card turns the act-3 event into this one (LanternKey.ModifyNextEvent).</summary>
    private const string WarHistorian = "EVENT.WAR_HISTORIAN_REPY";

    /// <summary>
    /// Predicts the event of the room being entered. Null when the save is not a single-player room-entry save
    /// (later saves of the same room already count the pulled event) or lacks the event lists.
    /// </summary>
    public static EventGuess? Predict(ParsedSave save)
    {
        if (save.Events is not { } ev || !ev.IsRoomEntrySave || save.PlayerNetIds.Count > 1) return null;
        var state = save.State;

        if (save.Map is { Current: { } cur } map && map.Nodes.TryGetValue(cur, out var node) && node.Type == MapNodeType.Ancient)
            return ev.AncientId is { } a ? new EventGuess(a, true, []) : null;

        if (ev.EventIds.Count == 0) return null;
        var facts = new Facts(ev.ActIndex, state.Gold, state.Hp, state.MaxHp, ev.TotalHistory, state, save.Potions.Select(p => p.Id).ToList());
        var guess = Pull(ev.EventIds, ev.EventsVisited, ev.EventsSeen.ToHashSet(StringComparer.Ordinal), facts, out _);
        if (ev.ActIndex == 2 && state.Deck.Any(c => c.Id == "CARD.LANTERN_KEY")) return new EventGuess(WarHistorian, true, []);
        return guess;
    }

    /// <summary>
    /// The pull loop. <paramref name="nextIndex"/> is the <c>events_visited</c> value after the pull when the guess is
    /// right (the game stores the index past the event it took).
    /// </summary>
    internal static EventGuess Pull(IReadOnlyList<string> ids, int startIndex, IReadOnlySet<string> seen, Facts facts, out int nextIndex)
    {
        var alternatives = new List<string>();
        var certain = true;
        string? pick = null;
        nextIndex = startIndex + 1;
        for (int k = 0, i = startIndex; k < ids.Count; k++, i++)
        {
            var e = ids[i % ids.Count];
            if (seen.Contains(e)) continue;
            var allowed = IsAllowed(e, facts);
            if (allowed == Allowed.No) continue;
            if (allowed == Allowed.ProbablyNot)
            {
                // Skipped on an approximate check: it is the answer if the check is wrong.
                certain = false;
                if (pick is null) alternatives.Add(e);
                continue;
            }
            if (pick is null)
            {
                pick = e;
                nextIndex = i + 1;
                if (allowed == Allowed.Yes) break;
                certain = false; // kept on an approximate check: the next allowed event is the fallback
                continue;
            }
            alternatives.Add(e);
            break;
        }
        // Every event used up: the game repeats the one at the start index (RoomSet.EnsureNextEventIsValid warns).
        return new EventGuess(pick ?? ids[startIndex % ids.Count], certain, alternatives);
    }

    internal enum Allowed { Yes, No, Probably, ProbablyNot }

    /// <param name="Potions">Potion ids held; null when unknown (a replay of past pulls), which makes potion checks approximate.</param>
    internal sealed record Facts(int Act, int Gold, int Hp, int MaxHp, int TotalFloor, RunState State, IReadOnlyList<string>? Potions);

    /// <summary>
    /// <c>EventModel.IsAllowed</c> overrides of v0.107.1 (single player). Checks that need card/relic internals the save
    /// doesn't carry (enchantability, tradable relics, pets) are approximated and say so (Probably / ProbablyNot).
    /// </summary>
    internal static Allowed IsAllowed(string eventId, Facts f)
    {
        static Allowed B(bool b) => b ? Allowed.Yes : Allowed.No;
        var deck = f.State.Deck;
        // Potions unknown (replay): potion checks can't decide, so they say Probably.
        Allowed Potions(Func<IReadOnlyList<string>, bool> test) => f.Potions is { } p ? B(test(p)) : Allowed.Probably;
        bool HasPotion() => f.Potions is not { } p || p.Count > 0;
        int BasicStrikes() => deck.Count(c => c.Id.StartsWith("CARD.STRIKE_", StringComparison.Ordinal));
        int BasicDefends() => deck.Count(c => c.Id.StartsWith("CARD.DEFEND_", StringComparison.Ordinal));
        bool UnenchantedBasic() => deck.Any(c => c.EnchantmentId is null
            && (c.Id.StartsWith("CARD.STRIKE_", StringComparison.Ordinal) || c.Id.StartsWith("CARD.DEFEND_", StringComparison.Ordinal)));
        // Tradable relics: everything but the starter relic is the usual case; a few relics are not tradable.
        int Tradable() => Math.Max(0, f.State.Relics.Count - 1);

        return eventId switch
        {
            "EVENT.BRAIN_LEECH" => B(f.Act < 2),
            "EVENT.ROOM_FULL_OF_CHEESE" => B(f.Act < 2),
            "EVENT.CRYSTAL_SPHERE" => B(f.Gold >= 100 && f.Act > 0),
            "EVENT.DOLL_ROOM" => B(f.Act == 1),
            "EVENT.WELCOME_TO_WONGOS" => B(f.Act == 1 && f.Gold >= 100),
            "EVENT.STONE_OF_ALL_TIME" => f.Act == 1 ? Potions(p => p.Count > 0) : Allowed.No,
            "EVENT.TEA_MASTER" => B(f.Act < 2 && f.Gold >= 150),
            "EVENT.POTION_COURIER" => B(f.Act > 0),
            "EVENT.SYMBIOTE" => B(f.Act > 0),
            "EVENT.THE_LEGENDS_WERE_TRUE" => B(f.Act == 0 && f.Hp >= 10),
            "EVENT.ENDLESS_CONVEYOR" => B(f.Gold >= 120),
            "EVENT.ZEN_WEAVER" => B(f.Gold >= 125),
            "EVENT.WATERLOGGED_SCRIPTORIUM" => B(f.Gold >= 55),
            "EVENT.WHISPERING_HOLLOW" => B(f.Gold >= 44),
            "EVENT.COLOSSAL_FLOWER" => B(f.Hp >= 19),
            "EVENT.ROUND_TEA_PARTY" => B(f.Hp >= 12),
            "EVENT.TRASH_HEAP" => B(f.Hp > 5),
            "EVENT.UNREST_SITE" => B(f.Hp <= f.MaxHp * 0.7m),
            "EVENT.PUNCH_OFF" => B(f.TotalFloor >= 6),
            "EVENT.THE_FUTURE_OF_POTIONS" => Potions(p => p.Count >= 2),
            "EVENT.WAR_HISTORIAN_REPY" => Allowed.No,
            "EVENT.FAKE_MERCHANT" => f.Act < 1 ? Allowed.No : f.Gold >= 100 ? Allowed.Yes : Potions(p => p.Contains("POTION.FOUL_POTION")),
            "EVENT.AMALGAMATOR" => BasicStrikes() >= 2 && BasicDefends() >= 2 ? Allowed.Yes : Allowed.ProbablyNot,          // Strike/Defend tags
            "EVENT.BYRDONIS_NEST" => deck.Any(c => c.Id == "CARD.BYRDONIS_EGG") ? Allowed.No : Allowed.Probably,             // pet relics
            "EVENT.WOOD_CARVINGS" => BasicStrikes() + BasicDefends() > 0 ? Allowed.Yes : Allowed.ProbablyNot,               // basic + removable
            "EVENT.FIELD_OF_MAN_SIZED_HOLES" or "EVENT.SPIRALING_WHIRLPOOL" => UnenchantedBasic() ? Allowed.Yes : Allowed.ProbablyNot,
            "EVENT.GRAVE_OF_THE_FORGOTTEN" => deck.Any(c => c.EnchantmentId is null) ? Allowed.Probably : Allowed.ProbablyNot,
            "EVENT.LUMINOUS_CHOIR" => f.Gold >= 149 ? Allowed.Probably : Allowed.No,                                        // relic bag not empty
            "EVENT.MORPHIC_GROVE" => f.Gold >= 100 && deck.Count >= 2 ? Allowed.Probably : Allowed.No,                      // transformable cards
            "EVENT.RANWID_THE_ELDER" => f.Act != 0 && f.Gold >= 100 && HasPotion() ? (Tradable() >= 1 ? Allowed.Probably : Allowed.ProbablyNot) : Allowed.No,
            "EVENT.RELIC_TRADER" => f.Act != 0 ? (Tradable() >= 5 ? Allowed.Probably : Allowed.ProbablyNot) : Allowed.No,
            "EVENT.SLIPPERY_BRIDGE" => f.TotalFloor > 6 && deck.Count > 0 ? Allowed.Probably : Allowed.No,
            "EVENT.COLORFUL_PHILOSOPHERS" => Allowed.Probably, // more than one character pool unlocked (its epoch is already filtered)
            _ => Allowed.Yes,
        };
    }
}
