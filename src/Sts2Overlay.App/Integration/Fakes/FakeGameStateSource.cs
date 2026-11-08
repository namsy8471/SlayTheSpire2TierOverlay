using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Integration.Fakes;

/// <summary>Demo game state: a fixed run whose screen hint follows <see cref="Screen"/> (switched from the tray menu).</summary>
public sealed class FakeGameStateSource(DemoScreen screen) : IGameStateSource
{
    private volatile GameStateSnapshot _current = Build(screen);
    private volatile int _screen = (int)screen;

    public event EventHandler? Changed;

    public GameStateSnapshot Current => _current;

    /// <summary>Read by <see cref="FakeCardRecognizer"/> on the capture thread, hence volatile.</summary>
    public DemoScreen Screen => (DemoScreen)_screen;

    public void SetScreen(DemoScreen value)
    {
        _screen = (int)value;
        _current = Build(value);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Start() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
    }

    private static GameStateSnapshot Build(DemoScreen s) => new(
        DemoScenario.Run,
        Map: null,
        s switch
        {
            DemoScreen.Reward => ScreenHint.RewardLikely,
            DemoScreen.Shop => ScreenHint.ShopLikely,
            _ => ScreenHint.Map,
        },
        OfferContext.EliteReward,
        DateTime.Now);
}
