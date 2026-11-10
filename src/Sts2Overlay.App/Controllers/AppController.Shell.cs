using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// The app shell's standing notices and state the tray needs: hotkeys another program holds, a game that is not 16:9,
/// click-through, the open tray menu, and the panel's first placement on the game's monitor.
///
/// <para>The notices go to <see cref="ViewModels.PanelViewModel.ShellNotice"/>, which (unlike
/// <see cref="ShowMessage"/>) does not time out: it stays until the cause is gone.</para>
/// </summary>
public sealed partial class AppController
{
    /// <summary>How far the game's width/height may be from 16:9 before recognition is likely to miss things.</summary>
    internal const double AspectTolerance = 0.015;

    private IReadOnlyList<string> _failedHotkeys = [];
    private bool _overlaySpikeRunning;
    private bool _aspectOff;

    /// <summary>Ctrl+F7 / tray "클릭 통과" is on.</summary>
    public bool ClickThrough => _clickThrough;

    /// <summary>The tray menu is open: the topmost re-assert must not lift our windows above it.</summary>
    public bool TrayMenuOpen { get; set; }

    /// <summary>The hotkeys another program held at the last registration (empty: all registered).</summary>
    public void SetHotkeyFailures(IReadOnlyList<string> failed, bool overlaySpikeRunning)
    {
        _failedHotkeys = failed.ToArray();
        _overlaySpikeRunning = overlaySpikeRunning;
        RefreshShellNotice();
    }

    /// <summary>Panel text for hotkeys another program holds, or null when there are none.</summary>
    internal static string? HotkeyNotice(IReadOnlyList<string> failed, bool overlaySpikeRunning)
    {
        if (failed.Count == 0) return null;
        var keys = string.Join(", ", failed);
        return Loc.T($"단축키 등록 실패 (다른 프로그램이 사용 중): {keys} · 트레이 메뉴로도 켜고 끌 수 있습니다",
                   $"Hotkeys not registered (used by another program): {keys} · the tray menu has the same switches")
               + (overlaySpikeRunning ? Loc.T(" · 예전 OverlaySpike를 닫아 주세요", " · please close the old OverlaySpike") : "");
    }

    /// <summary>
    /// True when <paramref name="width"/> x <paramref name="height"/> is not 16:9 within <see cref="AspectTolerance"/>.
    /// Every recognition coordinate assumes 16:9 (Vision.RefTransform), so 16:10, 21:9 or a maximised window
    /// (1920x1017) can miss some screens.
    /// </summary>
    internal static bool AspectIsOff(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        var ratio = (double)width / height / (16.0 / 9.0);
        return Math.Abs(ratio - 1) > AspectTolerance;
    }

    internal static string AspectNotice() => Loc.T(
        "화면 비율이 16:9가 아니어서 일부 화면의 카드·표시 인식이 안 될 수 있습니다 (게임 해상도를 16:9로 설정)",
        "The game is not 16:9, so cards and marks may not be recognized on some screens (set a 16:9 resolution in the game)");

    /// <summary>The standing notice lines (null when there is nothing to say), in the current language.</summary>
    internal static string? ShellNoticeText(IReadOnlyList<string> failedHotkeys, bool overlaySpikeRunning, bool aspectOff)
    {
        var lines = new List<string>();
        if (HotkeyNotice(failedHotkeys, overlaySpikeRunning) is { } keys) lines.Add(keys);
        if (aspectOff) lines.Add(AspectNotice());
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Rebuilt after a change and on a language change (<see cref="OnTrackerChanged"/> runs then).</summary>
    private void RefreshShellNotice() => _vm.ShellNotice = ShellNoticeText(_failedHotkeys, _overlaySpikeRunning, _aspectOff);

    /// <summary>
    /// Called with every game-window change: the aspect-ratio notice (kept while minimised, gone with the game), and the
    /// panel's first placement on the game's monitor.
    /// </summary>
    private void UpdateShellForGame(GameWindowInfo? info)
    {
        if (info is null)
        {
            _aspectOff = false;
        }
        else if (!info.IsSimulated && !info.IsMinimized && !info.CaptureRect.IsEmpty)
        {
            var off = AspectIsOff(info.CaptureRect.Width, info.CaptureRect.Height);
            if (off != _aspectOff) AppLog.Info($"game aspect {info.CaptureRect.Width}x{info.CaptureRect.Height}: {(off ? "not 16:9, notice shown" : "16:9")}");
            _aspectOff = off;
            _panel.FollowGame(info.CaptureRect);
        }
        RefreshShellNotice();
    }
}
