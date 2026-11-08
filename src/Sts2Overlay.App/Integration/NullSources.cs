namespace Sts2Overlay.App.Integration;

/// <summary>Placeholder until the real save/log tracker is wired: no run, no hint (only manual Ctrl+F10 recognition).</summary>
public sealed class NullGameStateSource : IGameStateSource
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public GameStateSnapshot Current => GameStateSnapshot.Empty;

    public void Start()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>Placeholder until Vision is wired: never recognises anything.</summary>
public sealed class NullCardRecognizer : ICardRecognizer
{
    public RecognitionResult? Recognize(CapturedFrame frame, string? characterHint) => null;
}
