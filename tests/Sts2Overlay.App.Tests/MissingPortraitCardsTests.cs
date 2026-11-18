using Sts2Overlay.App.Integration;
using Sts2Overlay.Core.Model;
using Sts2Overlay.GameState.Tracking;

namespace Sts2Overlay.App.Tests;

public class MissingPortraitCardsTests
{
    private sealed class Replay(params string[][] rewards) : ICardOfferSink
    {
        public void OnCardOfferSeen(IReadOnlyList<CardRef> offered) { }
        public IReadOnlyDictionary<string, int>? ReplayedRewardUpgrades(IReadOnlyList<string> shown) => null;
        public IReadOnlyList<IReadOnlyList<string>> ReplayedRewardCards(int count) => rewards.Where(r => r.Length == count).ToList();
    }

    private static RecognizedCard Card(string id, int x, bool missing = false) =>
        new(new CardRef(id, 0), new PixelRect(x, 0, 100, 150), 0.9) { MissingPortrait = missing };

    [Fact]
    public void Placeholders_take_the_one_replayed_reward_that_fits_and_are_left_out_otherwise()
    {
        // The screen read Cho Firm from its art; the other two show the placeholder (named after any card without art).
        var screen = new RecognitionResult(ScreenKind.Reward, [Card("CARD.NAIL", 0, true), Card("CARD.NAIL", 200, true), Card("CARD.CHO_FIRM", 400)]);
        string[] fits = ["CARD.HIDDEN_ROOTS", "CARD.WHISTLING_ARROWS", "CARD.CHO_FIRM"];

        Assert.Equal(fits, MissingPortraitCards.Resolve(screen, new Replay(fits, ["CARD.A", "CARD.B", "CARD.C"])).Cards.Select(c => c.Card.Id));
        // No replay, a replay that disagrees with the card read from its art, or two rewards that both fit: only the read card.
        foreach (var replay in new ICardOfferSink?[] { null, new Replay(["CARD.A", "CARD.B", "CARD.C"]),
                     new Replay(fits, ["CARD.X", "CARD.Y", "CARD.CHO_FIRM"]) })
            Assert.Equal(["CARD.CHO_FIRM"], MissingPortraitCards.Resolve(screen, replay).Cards.Select(c => c.Card.Id));
        // Another screen (shop, grid): placeholders are left out even when a reward replay exists.
        Assert.Equal(["CARD.CHO_FIRM"], MissingPortraitCards.Resolve(screen with { ScreenKind = ScreenKind.Shop }, new Replay(fits)).Cards.Select(c => c.Card.Id));
        Assert.Equal(ScreenKind.Other, MissingPortraitCards.Resolve(new RecognitionResult(ScreenKind.Grid, [Card("CARD.NAIL", 0, true)]), null).ScreenKind);
    }
}
