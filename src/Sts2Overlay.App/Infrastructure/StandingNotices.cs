namespace Sts2Overlay.App.Infrastructure;

/// <summary>
/// Problems the user should keep seeing until they are fixed (not a toast that fades after a few seconds): the game's
/// save/log format could not be read, settings.json was broken. The panel's resource banner shows them
/// (<see cref="Services.LocalResourceService"/>). Each notice is a text factory, so it follows a language change.
/// Thread-safe: the game-state side sets its notice from a background thread.
/// </summary>
public static class StandingNotices
{
    public const string GameFormat = "game-format";
    public const string Settings = "settings";

    private static readonly Lock Gate = new();
    private static readonly List<(string Key, Func<string> Text)> Items = [];

    /// <summary>Raised (on any thread) when a notice was added, replaced or removed.</summary>
    public static event Action? Changed;

    /// <summary>Adds or replaces the notice <paramref name="key"/>; null removes it.</summary>
    public static void Set(string key, Func<string>? text)
    {
        lock (Gate)
        {
            var i = Items.FindIndex(n => n.Key == key);
            if (text is null)
            {
                if (i < 0) return;
                Items.RemoveAt(i);
            }
            else if (i >= 0) Items[i] = (key, text);
            else Items.Add((key, text));
        }
        Changed?.Invoke();
    }

    /// <summary>The current notices in the current language, oldest first.</summary>
    public static IReadOnlyList<string> Lines()
    {
        lock (Gate) return Items.Select(n => n.Text()).ToList();
    }
}
