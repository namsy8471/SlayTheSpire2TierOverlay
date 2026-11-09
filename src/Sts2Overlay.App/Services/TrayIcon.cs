using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Sts2Overlay.App.Integration.Fakes;
using Sts2Overlay.Core.Localization;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Notification-area icon (WinForms NotifyIcon; WPF has none). It works on the WPF UI thread because WPF's
/// Dispatcher pumps ordinary Win32 messages, which is all NotifyIcon needs.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly IntPtr _hIcon;
    private readonly Icon _drawnIcon;
    private readonly string _versionText;
    private readonly bool _devBuild;
    private readonly ToolStripMenuItem _demoReward, _demoShop, _demoOther, _demoOff, _toggle, _scaleAuto, _outlines, _onTop, _mapOverlay, _inCapture;
    private readonly ToolStripMenuItem _applyUpdate;
    private readonly ToolStripMenuItem _demo, _scaleMenu, _recognize, _reloadPack, _version, _checkUpdate, _exit;
    private readonly ToolStripMenuItem _languageMenu, _languageAuto, _languageKor, _languageEng;
    private readonly List<ToolStripMenuItem> _scaleItems = [];

    // Inputs of the dynamic labels, remembered so RelabelMenu can rebuild them in the new language.
    private bool _userHidden;
    private double _uiScale = 1.0;
    private bool _inCaptureForcedHidden;
    private string? _updateVersion;

    /// <param name="versionText">Shown as the disabled "버전 ..." item, e.g. "0.1.0".</param>
    /// <param name="devBuild">Not an installed build: the version item says "(개발용)".</param>
    public TrayIcon(string versionText, bool devBuild = false)
    {
        (_drawnIcon, _hIcon) = LoadIcon();
        _versionText = versionText;
        _devBuild = devBuild;

        // Labels are written by RelabelMenu (below), so a language change only has to call it again.
        _toggle = new ToolStripMenuItem("", null, (_, _) => ToggleVisibilityRequested?.Invoke());
        _demoReward = new ToolStripMenuItem("", null, (_, _) => DemoRequested?.Invoke(DemoScreen.Reward));
        _demoShop = new ToolStripMenuItem("", null, (_, _) => DemoRequested?.Invoke(DemoScreen.Shop));
        _demoOther = new ToolStripMenuItem("", null, (_, _) => DemoRequested?.Invoke(DemoScreen.Other));
        _demoOff = new ToolStripMenuItem("", null, (_, _) => DemoRequested?.Invoke(null));
        _demo = new ToolStripMenuItem("");
        _demo.DropDownItems.AddRange([_demoReward, _demoShop, _demoOther, new ToolStripSeparator(), _demoOff]);

        // UI 배율: 자동 + presets. Tag holds the value (null = auto).
        _scaleMenu = new ToolStripMenuItem("");
        _scaleAuto = new ToolStripMenuItem("", null, (_, _) => UiScaleRequested?.Invoke(null));
        _scaleMenu.DropDownItems.Add(_scaleAuto);
        _scaleMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var preset in Views.UiScale.Presets)
        {
            var item = new ToolStripMenuItem($"{preset * 100:0}%", null, (_, _) => UiScaleRequested?.Invoke(preset)) { Tag = preset };
            _scaleItems.Add(item);
            _scaleMenu.DropDownItems.Add(item);
        }

        // 언어 / Language: auto (follow the game) or a fixed language. Tag holds the settings value.
        // The menu title and the language names stay bilingual/native, so the menu can be found whatever language is on.
        _languageMenu = new ToolStripMenuItem("언어 / Language");
        _languageAuto = new ToolStripMenuItem("", null, (_, _) => LanguageRequested?.Invoke(GameLanguage.Auto)) { Tag = GameLanguage.Auto };
        _languageKor = new ToolStripMenuItem("한국어", null, (_, _) => LanguageRequested?.Invoke(GameLanguage.Korean)) { Tag = GameLanguage.Korean };
        _languageEng = new ToolStripMenuItem("English", null, (_, _) => LanguageRequested?.Invoke(GameLanguage.English)) { Tag = GameLanguage.English };
        _languageMenu.DropDownItems.AddRange([_languageAuto, new ToolStripSeparator(), _languageKor, _languageEng]);

        _outlines = new ToolStripMenuItem("", null, (_, _) => DebugOutlinesRequested?.Invoke());
        _onTop = new ToolStripMenuItem("", null, (_, _) => AlwaysOnTopRequested?.Invoke());
        _mapOverlay = new ToolStripMenuItem("", null, (_, _) => MapOverlayRequested?.Invoke());
        _inCapture = new ToolStripMenuItem("", null, (_, _) => ShowInCaptureRequested?.Invoke());
        _recognize = new ToolStripMenuItem("", null, (_, _) => RecognizeRequested?.Invoke());
        // No capture / log items: players never need them. Debug captures are a settings.json switch (debugCaptures).
        _reloadPack = new ToolStripMenuItem("", null, (_, _) => ReloadPackRequested?.Invoke());
        _version = new ToolStripMenuItem("") { Enabled = false };
        _checkUpdate = new ToolStripMenuItem("", null, (_, _) => CheckUpdateRequested?.Invoke());
        _applyUpdate = new ToolStripMenuItem("", null, (_, _) => ApplyUpdateRequested?.Invoke()) { Visible = false };
        _exit = new ToolStripMenuItem("", null, (_, _) => ExitRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _toggle,
            _onTop,
            _mapOverlay,
            _inCapture,
            _scaleMenu,
            _languageMenu,
            _recognize,
            _outlines,
            _demo,
            new ToolStripSeparator(),
            _reloadPack,
            new ToolStripSeparator(),
            _version,
            _checkUpdate,
            _applyUpdate,
            new ToolStripSeparator(),
            _exit,
        ]);

        _icon = new NotifyIcon
        {
            Icon = _drawnIcon,
            Text = Loc.T("STS2 티어 오버레이", "STS2 Tier Overlay"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ToggleVisibilityRequested?.Invoke();
        RelabelMenu();
    }

    /// <summary>Writes every menu label in the current language (at start and after a language change).</summary>
    public void RelabelMenu()
    {
        _toggle.Text = ToggleText(_userHidden);
        _demo.Text = Loc.T("데모 모드", "Demo mode");
        _demoReward.Text = Loc.T("보상 화면", "Reward screen");
        _demoShop.Text = Loc.T("상점 화면", "Shop screen");
        _demoOther.Text = Loc.T("다른 화면 (배지 없음)", "Other screen (no badges)");
        _demoOff.Text = Loc.T("데모 끄기 (실제 게임)", "Demo off (real game)");
        _scaleMenu.Text = Loc.T("UI 배율", "UI scale");
        _scaleAuto.Text = ScaleAutoText(_uiScale);
        _languageAuto.Text = Loc.T("자동 (게임 설정 따름)", "Auto (follow the game)");
        _outlines.Text = Loc.T("인식 영역 표시 (Ctrl+F11)", "Show recognition areas (Ctrl+F11)");
        _onTop.Text = Loc.T("항상 맨 앞에 표시 (Ctrl+F6)", "Always on top (Ctrl+F6)");
        _mapOverlay.Text = Loc.T("맵·휴식처 위 추천 표시 (Ctrl+F5)", "Show advice on Map / Rest Site (Ctrl+F5)");
        _inCapture.Text = InCaptureLabel(_inCapture.Checked, _inCaptureForcedHidden);
        _recognize.Text = Loc.T("지금 인식 (Ctrl+F10)", "Recognize now (Ctrl+F10)");
        _reloadPack.Text = Loc.T("데이터 팩 다시 읽기", "Reload data pack");
        _version.Text = Loc.T($"버전 {_versionText}", $"Version {_versionText}") + (_devBuild ? Loc.T(" (개발용)", " (dev)") : "");
        _checkUpdate.Text = CheckUpdateLabel();
        _applyUpdate.Text = ApplyUpdateText(_updateVersion);
        _exit.Text = Loc.T("종료", "Exit");
    }

    public event Action? ExitRequested;
    public event Action? ToggleVisibilityRequested;
    public event Action? RecognizeRequested;

    /// <summary>UI 배율 chosen: null = auto, else 1.0 / 1.25 / ...</summary>
    public event Action<double?>? UiScaleRequested;
    public event Action? ReloadPackRequested;

    /// <summary>Null = demo off.</summary>
    public event Action<DemoScreen?>? DemoRequested;

    /// <summary>"인식 영역 표시": debug outlines of the recognised card and price boxes.</summary>
    public event Action? DebugOutlinesRequested;

    public void SetDebugOutlines(bool on) => _outlines.Checked = on;

    public event Action? AlwaysOnTopRequested;

    public void SetAlwaysOnTop(bool on) => _onTop.Checked = on;

    /// <summary>"맵 위 추천 표시": the recommended node and path drawn on the game's map screen.</summary>
    public event Action? MapOverlayRequested;

    public void SetMapOverlay(bool on) => _mapOverlay.Checked = on;

    /// <summary>"방송·캡처에 오버레이 표시": the overlay in OBS / Discord / screenshots.</summary>
    public event Action? ShowInCaptureRequested;

    /// <param name="on">The user's choice.</param>
    /// <param name="forcedHidden">Recognition is on the GDI fallback, so the overlay is hidden from capture for now.</param>
    public void SetShowInCapture(bool on, bool forcedHidden)
    {
        _inCapture.Checked = on;
        _inCaptureForcedHidden = forcedHidden;
        _inCapture.Text = InCaptureLabel(on, forcedHidden);
    }

    private static string InCaptureLabel(bool on, bool forcedHidden)
    {
        var text = Loc.T("방송·캡처에 오버레이 표시", "Show overlay in streams/captures");
        return on && forcedHidden ? text + Loc.T(" (지금은 숨김: 호환 캡처 사용 중)", " (hidden now: compatibility capture in use)") : text;
    }

    private bool _updateChecking;

    private static string CheckUpdateText => Loc.T("업데이트 확인", "Check for updates");

    /// <summary>"업데이트 확인": check for a new version now.</summary>
    public event Action? CheckUpdateRequested;

    private string CheckUpdateLabel() => _updateChecking ? Loc.T("업데이트 확인 중…", "Checking for updates…") : CheckUpdateText;

    /// <summary>While a check or download runs: "업데이트 확인 중…", disabled.</summary>
    public void SetUpdateChecking(bool checking)
    {
        _updateChecking = checking;
        _checkUpdate.Enabled = !checking;
        _checkUpdate.Text = CheckUpdateLabel();
    }

    /// <summary>"업데이트 적용 (재시작)": shown once an update was downloaded.</summary>
    public event Action? ApplyUpdateRequested;

    /// <summary>Shows "업데이트 적용 (재시작) v{version}" when an update is ready; null hides it.</summary>
    public void SetUpdateReady(string? version)
    {
        _updateVersion = version;
        _applyUpdate.Visible = version is not null;
        _applyUpdate.Text = ApplyUpdateText(version);
    }

    private static string ApplyUpdateText(string? version)
        => Loc.T($"업데이트 적용 (재시작) v{version}", $"Apply update (restart) v{version}");

    /// <summary>"언어 / Language" chosen: <c>auto</c>, <c>kor</c> or <c>eng</c> (<see cref="GameLanguage"/>).</summary>
    public event Action<string>? LanguageRequested;

    /// <summary>Check mark on the current "언어 / Language" choice (radio style: exactly one is checked).</summary>
    public void SetLanguageSetting(string setting)
    {
        var normalized = GameLanguage.NormalizeSetting(setting);
        foreach (var item in new[] { _languageAuto, _languageKor, _languageEng })
        {
            item.Checked = (string?)item.Tag == normalized;
        }
    }

    public void SetState(bool userHidden, DemoScreen? demo, double uiScale, bool uiScaleAuto, string tooltip)
    {
        _uiScale = uiScale;
        _userHidden = userHidden;
        _scaleAuto.Text = ScaleAutoText(uiScale);
        _scaleAuto.Checked = uiScaleAuto;
        foreach (var item in _scaleItems)
        {
            item.Checked = !uiScaleAuto && item.Tag is double v && Math.Abs(v - uiScale) < 0.001;
        }
        _toggle.Text = ToggleText(userHidden);
        _demoReward.Checked = demo == DemoScreen.Reward;
        _demoShop.Checked = demo == DemoScreen.Shop;
        _demoOther.Checked = demo == DemoScreen.Other;
        _demoOff.Checked = demo is null;
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private static string ScaleAutoText(double uiScale) => Loc.T($"자동 (현재 {uiScale * 100:0}%)", $"Auto (now {uiScale * 100:0}%)");

    private static string ToggleText(bool userHidden) => userHidden
        ? Loc.T("오버레이 보이기 (Ctrl+F8)", "Show overlay (Ctrl+F8)")
        : Loc.T("오버레이 숨기기 (Ctrl+F8)", "Hide overlay (Ctrl+F8)");

    public void ShowBalloon(string title, string text)
        => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    /// <summary>
    /// The app icon (Assets/Sts2Overlay.ico, embedded) at the tray's size, so it matches the exe and the installer.
    /// Falls back to drawing the same gold "S" tile if the resource cannot be read.
    /// </summary>
    private static (Icon Icon, IntPtr Handle) LoadIcon()
    {
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Sts2Overlay.ico");
            if (stream is not null)
            {
                using var ico = new Icon(stream, SystemInformation.SmallIconSize);
                // Copy into an icon whose handle we own (destroyed in Dispose, like the drawn one).
                using var bmp = ico.ToBitmap();
                var h = bmp.GetHicon();
                return (Icon.FromHandle(h), h);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or ExternalException)
        {
            Infrastructure.AppLog.Warn($"tray: could not load the app icon ({ex.Message}); drawing one");
        }
        return DrawIcon();
    }

    /// <summary>Draws a small gold "S" tier tile at runtime (fallback for <see cref="LoadIcon"/>).</summary>
    private static (Icon Icon, IntPtr Handle) DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r, r, 180, 90);
            path.AddArc(30 - r, 1, r, r, 270, 90);
            path.AddArc(30 - r, 30 - r, r, r, 0, 90);
            path.AddArc(1, 30 - r, r, r, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(241, 196, 15));
            g.FillPath(fill, path);
            using var font = new Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(30, 30, 30));
            var size = g.MeasureString("S", font);
            g.DrawString("S", font, text, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        // GetHicon creates a native icon we own; Icon.FromHandle only wraps it, so we destroy it ourselves on Dispose.
        var h = bmp.GetHicon();
        return (Icon.FromHandle(h), h);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawnIcon.Dispose();
        DestroyIcon(_hIcon);
    }
}
