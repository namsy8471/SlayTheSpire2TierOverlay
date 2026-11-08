using System.Windows.Interop;
using Sts2Overlay.App.Infrastructure;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey. They work while the GAME has focus, because Windows posts WM_HOTKEY to the
/// window that registered the key regardless of which window is in front.
///
/// We register on a hidden message-only window (parent HWND_MESSAGE) rather than on the panel, so hotkeys keep
/// working while the panel is hidden or being recreated. RegisterHotKey fails if another program (e.g. a still running
/// OverlaySpike) already owns the combination; that is logged and reported.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const uint VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("Sts2OverlayHotkeys")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers Ctrl+<paramref name="vk"/>. Returns false when the combination is taken.</summary>
    public bool RegisterCtrl(uint vk, string name, Action action)
    {
        var id = _nextId++;
        // MOD_NOREPEAT: holding the key down fires once, not at the keyboard repeat rate.
        if (!RegisterHotKey(_source.Handle, id, MOD_CONTROL | MOD_NOREPEAT, vk))
        {
            AppLog.Warn($"hotkey Ctrl+{name} is already used by another program");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("hotkey handler failed", ex);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
