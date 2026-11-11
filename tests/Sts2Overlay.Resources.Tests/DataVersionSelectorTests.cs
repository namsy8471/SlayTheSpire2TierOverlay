using Sts2Overlay.Resources;

namespace Sts2Overlay.Resources.Tests;

/// <summary>
/// <see cref="DataVersionSelector"/>: the newest data version not newer than the game; the oldest for an older game; the
/// newest for an unknown one. The version guard then warns only when the chosen pack differs from the game.
/// </summary>
public class DataVersionSelectorTests
{
    private static readonly string[] Shipped = ["v0.107.1", "v0.111.0", "sample"];

    [Theory]
    [InlineData("v0.107.1", "v0.107.1", DataVersionReason.NewestNotNewerThanGame, false)]
    [InlineData("v0.111.0", "v0.111.0", DataVersionReason.NewestNotNewerThanGame, false)]
    [InlineData("v0.109.1", "v0.107.1", DataVersionReason.NewestNotNewerThanGame, true)]
    [InlineData("v0.112", "v0.111.0", DataVersionReason.NewestNotNewerThanGame, true)]
    [InlineData("v0.100", "v0.107.1", DataVersionReason.GameOlderThanAll, true)]
    [InlineData(null, "v0.111.0", DataVersionReason.GameUnknown, false)]
    [InlineData("garbage", "v0.111.0", DataVersionReason.GameUnknown, false)]
    public void Picks_the_pack_for_the_game(string? game, string expected, DataVersionReason reason, bool warns)
    {
        var choice = DataVersionSelector.Select(Shipped, game);

        Assert.Equal(expected, choice.Version);
        Assert.Equal(reason, choice.Reason);
        var guard = VersionGuard.Evaluate(game, choice.Version, [], new HashSet<string>(), _ => true, []);
        Assert.Equal(warns, guard.VersionMismatch);
    }

    [Fact]
    public void Order_of_the_folder_listing_does_not_matter() =>
        Assert.Equal("v0.111.0", DataVersionSelector.Select(["v0.111.0", "v0.107.1"], "v0.111.0").Version);

    [Fact]
    public void No_pack_folders_gives_no_version() =>
        Assert.Null(DataVersionSelector.Select(["sample"], "v0.111.0").Version);
}
