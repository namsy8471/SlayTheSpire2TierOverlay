namespace Sts2Overlay.Core.Events;

/// <summary>
/// Which option sits on which button of an event page.
/// <para>
/// A page lists every option the game can put there, in button order (checked against captures: the game adds
/// them in the order of <c>GenerateInitialOptions</c>). Two kinds of entries are not buttons of their own:
/// a <c>X_LOCKED</c> key is the greyed-out face of <c>X</c> and takes its place, and an "only option when …" entry
/// (Self-Help Book's NO_OPTIONS, Relic Trader's PROCEED) replaces the whole list. When the screen shows exactly
/// as many buttons as the remaining options, button i is option i. Pages whose options are a random subset
/// (Endless Conveyor's dishes, Colorful Philosophers) never match a count by accident: the full list is longer
/// than any screen of them.
/// </para>
/// </summary>
public static class EventSlots
{
    public const string LockedSuffix = "_LOCKED";

    /// <summary>The page's buttons when nothing is known about the screen: locked faces and lone fallbacks left out.</summary>
    public static IReadOnlyList<EventOptionDef> Displayed(EventPage page)
    {
        var open = page.Options.Where(o => !IsLockedFace(o)).ToList();
        var main = open.Where(o => !IsLoneFallback(o)).ToList();
        return main.Count > 0 ? main : open;
    }

    /// <summary>
    /// The option keys on the <paramref name="buttons"/> buttons of the screen, top to bottom, or null when the page
    /// can't be lined up with them. <paramref name="locked"/> marks greyed-out buttons (vision): their key becomes
    /// the page's <c>X_LOCKED</c> key when the page has one.
    /// </summary>
    public static IReadOnlyList<string>? Keys(EventPage page, int buttons, IReadOnlyList<bool>? locked = null)
    {
        if (buttons <= 0) return null;
        var open = page.Options.Where(o => !IsLockedFace(o)).ToList();
        List<EventOptionDef>? slots = null;
        if (open.Count == buttons) slots = open;
        else
        {
            var main = open.Where(o => !IsLoneFallback(o)).ToList();
            var lone = open.Where(IsLoneFallback).ToList();
            if (main.Count == buttons) slots = main;
            else if (buttons == 1 && lone.Count == 1) slots = lone;
        }
        if (slots is null) return null;

        var keys = new string[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            var key = slots[i].Key;
            if (locked is not null && i < locked.Count && locked[i])
            {
                var face = page.Options.FirstOrDefault(o => o.Key == key + LockedSuffix)
                    ?? page.Options.FirstOrDefault(o => IsLockedFace(o) && o.Key.EndsWith("." + LockedSuffix.TrimStart('_'), StringComparison.Ordinal));
                key = face?.Key ?? key + LockedSuffix;
            }
            keys[i] = key;
        }
        return keys;
    }

    /// <summary><c>…options.X_LOCKED</c> or <c>…options.LOCKED</c>: the greyed-out face of another option.</summary>
    public static bool IsLockedFace(EventOptionDef o)
        => o.Key.EndsWith(LockedSuffix, StringComparison.Ordinal) || o.ShortName == LockedSuffix.TrimStart('_');

    /// <summary>An option that only shows up alone, instead of all the others.</summary>
    private static bool IsLoneFallback(EventOptionDef o)
        => o.Condition is { } c && (c.StartsWith("only option when", StringComparison.OrdinalIgnoreCase)
                                    || c.StartsWith("only when no", StringComparison.OrdinalIgnoreCase));
}
