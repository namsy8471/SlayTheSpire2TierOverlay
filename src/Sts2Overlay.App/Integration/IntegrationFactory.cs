using Sts2Overlay.App.Integration.Fakes;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// The single place where the App picks its game-state source and card recogniser.
/// LEAD: replace the Null* objects with adapters over Sts2Overlay.GameState / Sts2Overlay.Vision.
/// </summary>
public static class IntegrationFactory
{
    public static (IGameStateSource State, ICardRecognizer Recognizer) CreateReal()
        => (new NullGameStateSource(), new NullCardRecognizer());

    public static (IGameStateSource State, ICardRecognizer Recognizer) CreateDemo(DemoScreen screen)
    {
        var state = new FakeGameStateSource(screen);
        return (state, new FakeCardRecognizer(state));
    }
}
