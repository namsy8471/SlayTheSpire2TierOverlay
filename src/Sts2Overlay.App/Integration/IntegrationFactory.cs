using Sts2Overlay.App.Integration.Fakes;

namespace Sts2Overlay.App.Integration;

/// <summary>
/// The single place where the App picks its game-state source and card recogniser.
/// Real mode = save/log tracker (Sts2Overlay.GameState) + screen recogniser (Sts2Overlay.Vision).
/// </summary>
public static class IntegrationFactory
{
    public static (IGameStateSource State, ICardRecognizer Recognizer) CreateReal()
        => (new GameStateAdapter(), CreateRealRecognizer());

    /// <summary>A fresh recogniser that reads the current data/local files (used after the resources were rebuilt).</summary>
    public static VisionRecognizer CreateRealRecognizer() => new();

    public static (IGameStateSource State, ICardRecognizer Recognizer) CreateDemo(DemoScreen screen)
    {
        var state = new FakeGameStateSource(screen);
        return (state, new FakeCardRecognizer(state));
    }
}
