namespace Sts2Overlay.App.Integration.Fakes;

/// <summary>Demo recogniser: ignores the pixels and returns the cards of the current demo screen, placed on the frame.</summary>
public sealed class FakeCardRecognizer(FakeGameStateSource state) : ICardRecognizer
{
    public RecognitionResult? Recognize(CapturedFrame frame, string? characterHint) => state.Screen switch
    {
        DemoScreen.Reward => new RecognitionResult(ScreenKind.Reward, DemoScenario.RewardCards(frame.ScreenRect)),
        DemoScreen.Shop => new RecognitionResult(ScreenKind.Shop, DemoScenario.ShopCards(frame.ScreenRect)) { RemovalService = DemoScenario.RemovalCoin(frame.ScreenRect) },
        _ => RecognitionResult.Other,
    };
}
