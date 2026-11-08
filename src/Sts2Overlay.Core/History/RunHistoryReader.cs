namespace Sts2Overlay.Core.History;

/// <summary>Parses <c>.run</c> history files (PLAN §3.3, §7.7).</summary>
public static class RunHistoryReader
{
    public static RunHistory Parse(string json) => throw new NotImplementedException();

    public static RunHistory Read(Stream stream) => throw new NotImplementedException();

    public static RunHistory ReadFile(string path) => throw new NotImplementedException();
}

/// <summary>Which player is "me" in a run.</summary>
public static class LocalPlayer
{
    /// <summary>The anonymized id used for the user in test fixtures.</summary>
    public const long FixtureSelfId = 76561190000000001;

    /// <summary>
    /// Single player: the only player. Multiplayer: the player whose id equals <paramref name="steamId"/>
    /// (default <see cref="FixtureSelfId"/>), else the first player.
    /// </summary>
    public static RunPlayer Resolve(RunHistory run, long? steamId = null) => throw new NotImplementedException();
}
