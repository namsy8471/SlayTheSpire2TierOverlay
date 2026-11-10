using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// The app shell's standing notices and state the tray needs: hotkeys another program holds, the game's aspect ratio
/// option set away from 16:9, click-through, the open tray menu, and the panel's first placement on the game's monitor.
///
/// <para>The notices go to <see cref="ViewModels.PanelViewModel.ShellNotice"/>, which (unlike
/// <see cref="ShowMessage"/>) does not time out: it stays until the cause is gone.</para>
/// </summary>
public sealed partial class AppController
{
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
    /// True when the game's "Aspect Ratio" option (settings.save <c>aspect_ratio</c>) is known and is not the default
    /// <c>sixteen_by_nine</c>. With the default the game draws a fixed 1920x1080 view letterboxed into any window, which
    /// Vision.RefTransform already follows (16:10 monitors, ultrawides, a maximised 1920x1017 window all work); the other
    /// options change the game's layout, so some recognition positions are off. Unknown or unrecognised values: false.
    /// </summary>
    internal static bool AspectOptionIsOff(string? option)
    {
        var o = option?.Trim().Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return o is "sixteenbyten" or "fourbythree" or "twentyonebynine" or "auto";
    }

    /// <summary>The game's settings.save was read again (App's language watch): the notice follows its aspect option.</summary>
    public void SetGameAspectRatio(string? option)
    {
        if (!_ui.CheckAccess())
        {
            _ui.BeginInvoke(() => SetGameAspectRatio(option));
            return;
        }
        var off = AspectOptionIsOff(option);
        if (off != _aspectOff) AppLog.Info($"game aspect ratio option {option ?? "(unknown)"}: {(off ? "not 16:9, notice shown" : "16:9 or unknown")}");
        _aspectOff = off;
        RefreshShellNotice();
    }

    internal static string AspectNotice() => Loc.T(
        "게임의 화면 비율 옵션이 16:9가 아니어서 일부 화면의 카드·표시 인식이 어긋날 수 있습니다 (게임 설정 > 화면 비율을 16:9로 바꾸면 정확합니다)",
        "The game's aspect ratio option is not 16:9, so some cards and marks may be misplaced (set Settings > Aspect Ratio to 16:9 in the game for exact results)");

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
    /// Called with every game-window change: the panel's first placement on the game's monitor, and the notice text
    /// (rebuilt in the current language; a language change runs this too).
    /// </summary>
    private void UpdateShellForGame(GameWindowInfo? info)
    {
        if (info is { IsSimulated: false, IsMinimized: false, CaptureRect.IsEmpty: false }) _panel.FollowGame(info.CaptureRect);
        RefreshShellNotice();
    }
}
