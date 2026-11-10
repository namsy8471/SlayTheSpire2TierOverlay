using System.Windows.Interop;
using Sts2Overlay.App.Infrastructure;
using static Sts2Overlay.App.Interop.NativeMethods;

namespace Sts2Overlay.App.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey. They work while the GAME has focus, because Windows posts WM_HOTKEY to the
/// window that registered the key regardless of which window is in front.
///
/// A registered hotkey is taken away from EVERY other program (Ctrl+F5 would no longer refresh a browser page), so the
/// keys are only registered while the game or one of our own windows is in front (<see cref="SetActive"/>, driven by
/// <see cref="HotkeyPolicy"/>) and given back the moment another program comes to the front.
///
/// We register on a hidden message-only window (parent HWND_MESSAGE) rather than on the panel, so hotkeys keep
/// working while the panel is hidden or being recreated. RegisterHotKey fails if another program (e.g. a still running
/// OverlaySpike) already owns the combination; those keys are listed in <see cref="Failed"/> and tried again on the
/// next activation (the other program may have closed by then).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const uint VK_F5 = 0x74, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;

    private sealed record Binding(int Id, uint Vk, string Name, Action Action);

    private readonly HwndSource? _source;
    private readonly Func<int, uint, uint, bool> _register; // (id, modifiers, vk) → registered
    private readonly Action<int> _unregister;
    private readonly List<Binding> _bindings = [];
    private readonly Dictionary<int, Action> _actions = []; // the ids registered right now
    private readonly List<string> _failed = [];
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
        var hwnd = _source.Handle;
        _register = (id, mods, vk) => RegisterHotKey(hwnd, id, mods, vk);
        _unregister = id => UnregisterHotKey(hwnd, id);
    }

    /// <summary>Tests: the two native calls replaced, no window.</summary>
    internal HotkeyManager(Func<int, uint, uint, bool> register, Action<int> unregister)
    {
        _register = register;
        _unregister = unregister;
    }

    /// <summary>The keys are registered right now (the game or one of our windows is in front).</summary>
    public bool IsActive { get; private set; }

    /// <summary>"Ctrl+F5"... that another program held at the last activation. Kept while inactive.</summary>
    public IReadOnlyList<string> Failed => _failed;

    /// <summary>Adds Ctrl+<paramref name="vk"/>. It is registered while <see cref="IsActive"/>.</summary>
    public void AddCtrl(uint vk, string name, Action action)
    {
        var b = new Binding(_nextId++, vk, name, action);
        _bindings.Add(b);
        if (IsActive && !TryRegister(b))
        {
            _failed.Add(KeyName(b));
            AppLog.Warn($"hotkey {KeyName(b)} is already used by another program");
        }
    }

    /// <summary>
    /// Registers every key (true) or gives them all back (false). Only a change does something, so it can be called on
    /// every foreground change. Returns true when <see cref="Failed"/> changed.
    /// </summary>
    public bool SetActive(bool active)
    {
        if (active == IsActive) return false;
        IsActive = active;
        if (!active)
        {
            foreach (var id in _actions.Keys) _unregister(id);
            _actions.Clear();
            return false; // the failures stay listed until a retry succeeds
        }

        var failed = _bindings.Where(b => !TryRegister(b)).Select(KeyName).ToList();
        if (failed.SequenceEqual(_failed)) return false;
        _failed.Clear();
        _failed.AddRange(failed);
        AppLog.Warn(failed.Count > 0
            ? $"hotkeys already used by another program: {string.Join(", ", failed)}"
            : "hotkeys: every key registered now");
        return true;
    }

    private bool TryRegister(Binding b)
    {
        // MOD_NOREPEAT: holding the key down fires once, not at the keyboard repeat rate.
        if (!_register(b.Id, MOD_CONTROL | MOD_NOREPEAT, b.Vk)) return false;
        _actions[b.Id] = b.Action;
        return true;
    }

    private static string KeyName(Binding b) => "Ctrl+" + b.Name;

    /// <summary>Runs the action of a registered hotkey id (WM_HOTKEY). False when the id is not registered now.</summary>
    internal bool Invoke(int id)
    {
        if (!_actions.TryGetValue(id, out var action)) return false;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Error("hotkey handler failed", ex);
        }
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && Invoke(wParam.ToInt32())) handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        SetActive(false);
        if (_source is null) return;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

/// <summary>When the hotkeys are registered (pure, unit-tested).</summary>
public static class HotkeyPolicy
{
    /// <summary>
    /// The game is the foreground window, or one of our own windows is and counts as the game
    /// (<see cref="GameWindowInfo.IsOwnForeground"/>: opened from the game). Demo mode reports the game as always in
    /// front, so the keys stay registered there. No game: nothing to control, the keys belong to other programs.
    /// </summary>
    public static bool ShouldBeActive(GameWindowInfo? info) => info is { IsForeground: true } or { IsOwnForeground: true };
}
