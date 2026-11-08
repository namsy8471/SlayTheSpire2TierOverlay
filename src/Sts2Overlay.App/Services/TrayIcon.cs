using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
    private readonly ToolStripMenuItem _demoReward, _demoShop, _demoOther, _demoOff, _toggle, _scaleAuto;
    private readonly List<ToolStripMenuItem> _scaleItems = [];

    public TrayIcon()
    {
        (_drawnIcon, _hIcon) = DrawIcon();

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

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _toggle,
            scaleMenu,
            new ToolStripMenuItem("지금 인식 (Ctrl+F10)", null, (_, _) => RecognizeRequested?.Invoke()),
            new ToolStripMenuItem("캡처 저장 (Ctrl+F9)", null, (_, _) => SaveCaptureRequested?.Invoke()),
            demo,
            new ToolStripSeparator(),
            new ToolStripMenuItem("데이터 팩 다시 읽기", null, (_, _) => ReloadPackRequested?.Invoke()),
            new ToolStripMenuItem("캡처 폴더 열기", null, (_, _) => OpenCapturesRequested?.Invoke()),
            new ToolStripMenuItem("로그 열기", null, (_, _) => OpenLogRequested?.Invoke()),
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

    /// <summary>Draws a small gold "S" tier tile at runtime, so the repo needs no binary .ico file.</summary>
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
