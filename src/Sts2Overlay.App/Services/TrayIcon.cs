using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Sts2Overlay.App.Integration.Fakes;
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
    private readonly ToolStripMenuItem _demoReward, _demoShop, _demoOther, _demoOff, _toggle, _scaleAuto, _outlines, _onTop, _mapOverlay, _inCapture;
    private readonly ToolStripMenuItem _applyUpdate;
    private readonly List<ToolStripMenuItem> _scaleItems = [];

    /// <param name="versionText">Shown as the disabled "버전 ..." item, e.g. "0.1.0".</param>
    public TrayIcon(string versionText)
    {
        (_drawnIcon, _hIcon) = LoadIcon();

        _toggle = new ToolStripMenuItem("오버레이 숨기기 (Ctrl+F8)", null, (_, _) => ToggleVisibilityRequested?.Invoke());
        _demoReward = new ToolStripMenuItem("보상 화면", null, (_, _) => DemoRequested?.Invoke(DemoScreen.Reward));
        _demoShop = new ToolStripMenuItem("상점 화면", null, (_, _) => DemoRequested?.Invoke(DemoScreen.Shop));
        _demoOther = new ToolStripMenuItem("다른 화면 (배지 없음)", null, (_, _) => DemoRequested?.Invoke(DemoScreen.Other));
        _demoOff = new ToolStripMenuItem("데모 끄기 (실제 게임)", null, (_, _) => DemoRequested?.Invoke(null));
        var demo = new ToolStripMenuItem("데모 모드");
        demo.DropDownItems.AddRange([_demoReward, _demoShop, _demoOther, new ToolStripSeparator(), _demoOff]);

        // UI 배율: 자동 + presets. Tag holds the value (null = auto).
        var scaleMenu = new ToolStripMenuItem("UI 배율");
        _scaleAuto = new ToolStripMenuItem("자동", null, (_, _) => UiScaleRequested?.Invoke(null));
        scaleMenu.DropDownItems.Add(_scaleAuto);
        scaleMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var preset in Views.UiScale.Presets)
        {
            var item = new ToolStripMenuItem($"{preset * 100:0}%", null, (_, _) => UiScaleRequested?.Invoke(preset)) { Tag = preset };
            _scaleItems.Add(item);
            scaleMenu.DropDownItems.Add(item);
        }

        _outlines = new ToolStripMenuItem("인식 영역 표시 (Ctrl+F11)", null, (_, _) => DebugOutlinesRequested?.Invoke());
        _onTop = new ToolStripMenuItem("항상 맨 앞에 표시 (Ctrl+F6)", null, (_, _) => AlwaysOnTopRequested?.Invoke());
        _mapOverlay = new ToolStripMenuItem("맵·휴식처 위 추천 표시 (Ctrl+F5)", null, (_, _) => MapOverlayRequested?.Invoke());
        _inCapture = new ToolStripMenuItem(InCaptureText, null, (_, _) => ShowInCaptureRequested?.Invoke());

        _applyUpdate = new ToolStripMenuItem("업데이트 적용 (재시작)", null, (_, _) => ApplyUpdateRequested?.Invoke()) { Visible = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _toggle,
            _onTop,
            _mapOverlay,
            _inCapture,
            scaleMenu,
            new ToolStripMenuItem("지금 인식 (Ctrl+F10)", null, (_, _) => RecognizeRequested?.Invoke()),
            new ToolStripMenuItem("캡처 저장 (Ctrl+F9)", null, (_, _) => SaveCaptureRequested?.Invoke()),
            _outlines,
            demo,
            new ToolStripSeparator(),
            new ToolStripMenuItem("데이터 팩 다시 읽기", null, (_, _) => ReloadPackRequested?.Invoke()),
            new ToolStripMenuItem("캡처 폴더 열기", null, (_, _) => OpenCapturesRequested?.Invoke()),
            new ToolStripMenuItem("로그 열기", null, (_, _) => OpenLogRequested?.Invoke()),
            new ToolStripSeparator(),
            new ToolStripMenuItem($"버전 {versionText}") { Enabled = false },
            new ToolStripMenuItem("업데이트 확인", null, (_, _) => CheckUpdateRequested?.Invoke()),
            _applyUpdate,
            new ToolStripSeparator(),
            new ToolStripMenuItem("종료", null, (_, _) => ExitRequested?.Invoke()),
        ]);

        _icon = new NotifyIcon
        {
            Icon = _drawnIcon,
            Text = "STS2 티어 오버레이",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ToggleVisibilityRequested?.Invoke();
    }

    public event Action? ExitRequested;
    public event Action? ToggleVisibilityRequested;
    public event Action? RecognizeRequested;
    public event Action? SaveCaptureRequested;

    /// <summary>UI 배율 chosen: null = auto, else 1.0 / 1.25 / ...</summary>
    public event Action<double?>? UiScaleRequested;
    public event Action? ReloadPackRequested;
    public event Action? OpenCapturesRequested;
    public event Action? OpenLogRequested;

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

    private const string InCaptureText = "방송·캡처에 오버레이 표시";

    /// <summary>"방송·캡처에 오버레이 표시": the overlay in OBS / Discord / screenshots.</summary>
    public event Action? ShowInCaptureRequested;

    /// <param name="on">The user's choice.</param>
    /// <param name="forcedHidden">Recognition is on the GDI fallback, so the overlay is hidden from capture for now.</param>
    public void SetShowInCapture(bool on, bool forcedHidden)
    {
        _inCapture.Checked = on;
        _inCapture.Text = on && forcedHidden ? InCaptureText + " (지금은 숨김: 호환 캡처 사용 중)" : InCaptureText;
    }

    /// <summary>"업데이트 확인": check for a new version now.</summary>
    public event Action? CheckUpdateRequested;

    /// <summary>"업데이트 적용 (재시작)": shown once an update was downloaded.</summary>
    public event Action? ApplyUpdateRequested;

    /// <summary>Shows "업데이트 적용 (재시작) v{version}" when an update is ready; null hides it.</summary>
    public void SetUpdateReady(string? version)
    {
        _applyUpdate.Visible = version is not null;
        _applyUpdate.Text = $"업데이트 적용 (재시작) v{version}";
    }

    public void SetState(bool userHidden, DemoScreen? demo, double uiScale, bool uiScaleAuto, string tooltip)
    {
        _scaleAuto.Text = $"자동 (현재 {uiScale * 100:0}%)";
        _scaleAuto.Checked = uiScaleAuto;
        foreach (var item in _scaleItems)
        {
            item.Checked = !uiScaleAuto && item.Tag is double v && Math.Abs(v - uiScale) < 0.001;
        }
        _toggle.Text = userHidden ? "오버레이 보이기 (Ctrl+F8)" : "오버레이 숨기기 (Ctrl+F8)";
        _demoReward.Checked = demo == DemoScreen.Reward;
        _demoShop.Checked = demo == DemoScreen.Shop;
        _demoOther.Checked = demo == DemoScreen.Other;
        _demoOff.Checked = demo is null;
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

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
