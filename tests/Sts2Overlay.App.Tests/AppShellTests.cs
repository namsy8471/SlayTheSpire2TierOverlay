using System.IO;
using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The app shell's decision logic: when the global hotkeys are held, when our own foreground window counts as the
/// game, the standing notices, the panel's placement and the layer windows' placement check. The Win32 calls around
/// them are thin and checked in the real game.
/// </summary>
public class AppShellTests
{
    // ---- hotkeys: registered only while the game is in front ----

    private sealed class FakeHotkeyApi
    {
        public readonly HashSet<uint> TakenByOthers = [];
        public readonly Dictionary<int, uint> Registered = [];
        public int RegisterCalls;

        public bool Register(int id, uint mods, uint vk)
        {
            RegisterCalls++;
            Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, mods);
            if (TakenByOthers.Contains(vk) || !Registered.TryAdd(id, vk)) return false;
            return true;
        }

        public void Unregister(int id) => Assert.True(Registered.Remove(id), $"id {id} was not registered");
    }

    private static (HotkeyManager Keys, FakeHotkeyApi Api, List<string> Fired) Hotkeys()
    {
        var api = new FakeHotkeyApi();
        var keys = new HotkeyManager(api.Register, api.Unregister);
        var fired = new List<string>();
        keys.AddCtrl(HotkeyManager.VK_F5, "F5", () => fired.Add("F5"));
        keys.AddCtrl(HotkeyManager.VK_F8, "F8", () => fired.Add("F8"));
        return (keys, api, fired);
    }

    [Fact]
    public void Hotkeys_are_not_held_until_the_game_comes_to_the_front()
    {
        var (keys, api, _) = Hotkeys();
        Assert.False(keys.IsActive);
        Assert.Empty(api.Registered); // Ctrl+F5 still refreshes the browser

        keys.SetActive(true);
        Assert.Equal([HotkeyManager.VK_F5, HotkeyManager.VK_F8], api.Registered.Values.Order());

        keys.SetActive(false); // another program in front: every key given back
        Assert.Empty(api.Registered);
        Assert.False(keys.IsActive);
    }

    [Fact]
    public void Repeated_foreground_events_do_not_register_twice()
    {
        var (keys, api, _) = Hotkeys();
        keys.SetActive(true);
        keys.SetActive(true);
        Assert.Equal(2, api.RegisterCalls);
        keys.SetActive(false);
        keys.SetActive(false); // the fake asserts on unregistering an id that is not held
        keys.Dispose();
    }

    [Fact]
    public void A_hotkey_only_acts_while_it_is_registered()
    {
        var (keys, api, fired) = Hotkeys();
        keys.SetActive(true);
        var f8 = api.Registered.Single(p => p.Value == HotkeyManager.VK_F8).Key;
        Assert.True(keys.Invoke(f8));
        keys.SetActive(false);
        Assert.False(keys.Invoke(f8)); // a late WM_HOTKEY after giving the keys back does nothing
        Assert.Equal(["F8"], fired);
    }

    [Fact]
    public void A_key_another_program_holds_is_reported_and_retried_on_the_next_activation()
    {
        var (keys, api, _) = Hotkeys();
        api.TakenByOthers.Add(HotkeyManager.VK_F5);

        Assert.True(keys.SetActive(true)); // the failures changed: show the notice
        Assert.Equal(["Ctrl+F5"], keys.Failed);
        Assert.Equal([HotkeyManager.VK_F8], api.Registered.Values);

        Assert.False(keys.SetActive(false));
        Assert.Equal(["Ctrl+F5"], keys.Failed); // the notice stays while the game is in the background

        Assert.False(keys.SetActive(true)); // still held: same failures, nothing new to report
        keys.SetActive(false);

        api.TakenByOthers.Clear(); // the other program closed
        Assert.True(keys.SetActive(true));
        Assert.Empty(keys.Failed); // notice cleared
        Assert.Equal(2, api.Registered.Count);
    }

    [Fact]
    public void A_key_added_while_active_is_registered_at_once()
    {
        var (keys, api, _) = Hotkeys();
        keys.SetActive(true);
        keys.AddCtrl(HotkeyManager.VK_F10, "F10", () => { });
        Assert.Contains(HotkeyManager.VK_F10, api.Registered.Values);
    }

    private static GameWindowInfo Window(bool foreground = false, bool own = false, bool simulated = false, bool minimized = false)
        => new(IntPtr.Zero, 1, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), minimized, foreground, own, simulated);

    [Fact]
    public void Hotkeys_are_held_while_the_game_or_our_menu_opened_from_it_is_in_front()
    {
        Assert.True(HotkeyPolicy.ShouldBeActive(Window(foreground: true)));
        Assert.True(HotkeyPolicy.ShouldBeActive(Window(own: true)));
        Assert.False(HotkeyPolicy.ShouldBeActive(Window())); // a browser in front
        Assert.False(HotkeyPolicy.ShouldBeActive(null)); // no game running
        // Demo mode: the simulated game is always in front.
        Assert.True(HotkeyPolicy.ShouldBeActive(Window(foreground: true, simulated: true)));
    }

    // ---- our own foreground window counts only when it was opened from the game ----

    [Fact]
    public void The_tray_menu_opened_while_playing_keeps_the_overlay()
    {
        var rule = new OwnForegroundRule();
        Assert.False(rule.Update(gameForeground: true, ownForeground: false));
        // Clicking the tray icon activates the taskbar first, then our menu.
        Assert.False(rule.Update(gameForeground: false, ownForeground: false, taskbarForeground: true));
        Assert.True(rule.Update(gameForeground: false, ownForeground: true));
    }

    [Fact]
    public void The_tray_menu_opened_from_a_browser_does_not_show_the_overlay()
    {
        var rule = new OwnForegroundRule();
        rule.Update(gameForeground: true, ownForeground: false);
        rule.Update(gameForeground: false, ownForeground: false); // the browser
        rule.Update(gameForeground: false, ownForeground: false, taskbarForeground: true);
        Assert.False(rule.Update(gameForeground: false, ownForeground: true));

        // Back to the game, then the menu again: counts.
        rule.Update(gameForeground: true, ownForeground: false);
        Assert.True(rule.Update(gameForeground: false, ownForeground: true));

        // The game window went away (a restart): nothing came before.
        rule.Reset();
        Assert.False(rule.Update(gameForeground: false, ownForeground: true));
    }

    // ---- standing notices ----

    [Theory]
    [InlineData("sixteen_by_nine", false)] // the default: a fixed 1920x1080 view, letterboxed in any window
    [InlineData("sixteen_by_ten", true)]
    [InlineData("four_by_three", true)]
    [InlineData("twenty_one_by_nine", true)]
    [InlineData("auto", true)]
    [InlineData(" Sixteen_By_Ten ", true)] // tolerant spelling
    [InlineData("SixteenByNine", false)]
    [InlineData(null, false)] // file or key missing: unknown, no notice
    [InlineData("", false)]
    [InlineData("none", false)] // a value we do not know: no notice
    public void Aspect_ratio_notice_follows_the_games_aspect_option(string? option, bool off)
        => Assert.Equal(off, AppController.AspectOptionIsOff(option));

    [Theory]
    [InlineData("{ \"aspect_ratio\": \"sixteen_by_ten\", \"language\": \"kor\" }", "sixteen_by_ten")]
    [InlineData("{ \"language\": \"kor\" }", null)] // key missing
    [InlineData("{ \"aspect_ratio\": 3 }", null)] // not a string
    [InlineData("{ \"aspect_ratio\": ", null)] // half-written
    [InlineData("", null)]
    public void Aspect_ratio_option_is_read_from_settings_save(string json, string? expected)
        => Assert.Equal(expected, GameLanguage.ReadAspectRatio(json));

    [Fact]
    public void Aspect_ratio_notice_ignores_the_window_shape_with_the_default_option()
    {
        // A maximised 1920x1017 window or an ultrawide with the game's default option: the game letterboxes a
        // 1920x1080 view and recognition follows it. The notice depends on the option only, never on the window.
        using var _ = Loc.Use(Lang.Eng);
        Assert.Null(AppController.ShellNoticeText([], false, AppController.AspectOptionIsOff("sixteen_by_nine")));
        Assert.Null(AppController.ShellNoticeText([], false, AppController.AspectOptionIsOff(null)));
    }

    [Fact]
    public void Watcher_reads_the_aspect_option_and_reports_its_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "sts2-aspect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var watcher = new GameLanguageWatcher(root);
            var changes = 0;
            watcher.AspectRatioChanged += () => changes++;
            var dir = Path.Combine(root, "steam", "11111111111111111");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, GameLanguage.SettingsFileName);

            File.WriteAllText(file, "{ \"aspect_ratio\": \"twenty_one_by_nine\", \"language\": \"kor\" }");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-2));
            watcher.Poll();
            Assert.Equal("twenty_one_by_nine", watcher.AspectRatio);
            Assert.Equal(1, changes);

            // Language key missing: the aspect option is still read, the last known language kept.
            File.WriteAllText(file, "{ \"aspect_ratio\": \"sixteen_by_nine\" }");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
            watcher.Poll();
            Assert.Equal("sixteen_by_nine", watcher.AspectRatio);
            Assert.Equal("kor", watcher.Code);
            Assert.Equal(2, changes);

            // Half-written file: both kept, no change reported.
            File.WriteAllText(file, "{ \"aspect_ratio\": ");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            watcher.Poll();
            Assert.Equal("sixteen_by_nine", watcher.AspectRatio);
            Assert.Equal(2, changes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Shell_notice_names_the_keys_and_the_aspect_problem_in_english()
    {
        using var _ = Loc.Use(Lang.Eng);
        Assert.Null(AppController.ShellNoticeText([], false, false));
        var text = AppController.ShellNoticeText(["Ctrl+F5", "Ctrl+F7"], true, true)!;
        var lines = text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("Ctrl+F5, Ctrl+F7", lines[0]);
        Assert.Contains("OverlaySpike", lines[0]);
        Assert.StartsWith("The game's aspect ratio option is not 16:9", lines[1]);
    }

    [Fact]
    public void Shell_notice_in_korean()
    {
        using var _ = Loc.Use(Lang.Kor);
        Assert.StartsWith("단축키 등록 실패", AppController.ShellNoticeText(["Ctrl+F8"], false, false));
        Assert.StartsWith("게임의 화면 비율 옵션이 16:9가 아니어서", AppController.ShellNoticeText([], false, true));
    }

    // ---- panel placement ----

    [Fact]
    public void Default_panel_spot_is_on_the_given_monitors_right_edge()
    {
        // A secondary FHD monitor right of a 4K primary, 100 %.
        var work = new PixelRect(3840, 0, 1920, 1040);
        Assert.Equal((3840 + 1920 - 400 - 24, 140), PanelPlacement.DefaultPosition(work, 400, 1.0));
        // 4K at 150 %: margins in that monitor's scale.
        Assert.Equal((3840 - 600 - 36, 210), PanelPlacement.DefaultPosition(new PixelRect(0, 0, 3840, 2100), 600, 1.5));
    }

    [Fact]
    public void Scroll_area_shrinks_so_a_big_ui_scale_fits_a_small_screen()
    {
        // FHD (work area 1040 px), UI 200 %: the 620-DIP scroll area alone would be 1240 px.
        // Window now 1240 + 300 chrome = 1540 px.
        var max = PanelPlacement.ScrollMaxHeight(1040, 1540, 620, 2.0);
        Assert.Equal(370, max); // (1040 - 300) / 2
        Assert.True(max * 2.0 + 300 <= 1040);

        // A window that already fits keeps the design height.
        Assert.Equal(PanelPlacement.DesignScrollMaxHeight, PanelPlacement.ScrollMaxHeight(2100, 1200, 620, 1.5));
        // Never below the minimum, and a bad scale changes nothing.
        Assert.Equal(PanelPlacement.MinScrollHeight, PanelPlacement.ScrollMaxHeight(300, 1540, 620, 2.0));
        Assert.Equal(PanelPlacement.DesignScrollMaxHeight, PanelPlacement.ScrollMaxHeight(1040, 1540, 620, 0));
    }

    [Fact]
    public void Scroll_cap_is_stable_once_applied()
    {
        // After the cap the window is 370*2 + 300 = 1040 px: computing again gives the same cap (no resize loop).
        Assert.Equal(370, PanelPlacement.ScrollMaxHeight(1040, 1040, 370, 2.0));
    }

    // ---- layer windows stay on the game after a DPI change ----

    private sealed class FakeLayerWindow
    {
        public PixelRect Bounds = new(0, 0, 100, 100);
        public Func<PixelRect, PixelRect>? Override; // what the system makes of a SetWindowPos (null: as asked)
        public int Sets;
        public readonly List<string> Log = [];

        public LayerPlacement Placement() => new("badge", _ => Bounds, (_, r) =>
        {
            Sets++;
            Bounds = Override?.Invoke(r) ?? r;
        }, Log.Add);
    }

    private static readonly IntPtr Hwnd = new(1);

    [Fact]
    public void A_dpi_resize_during_the_first_move_is_undone_at_once()
    {
        var w = new FakeLayerWindow();
        var placement = w.Placement();
        var game = new PixelRect(1920, 0, 3840, 2160);
        // Crossing into the 150 % monitor: WPF applies the suggested rect (the size scaled by the DPI ratio) once.
        var first = true;
        w.Override = r =>
        {
            if (!first) return r;
            first = false;
            return r with { Width = r.Width * 2 / 3, Height = r.Height * 2 / 3 };
        };
        Assert.True(placement.Place(Hwnd, game));
        Assert.Equal(game, w.Bounds);
        Assert.Equal(2, w.Sets);
        Assert.Empty(w.Log); // fixed: nothing to report
    }

    [Fact]
    public void Same_rect_is_checked_again_and_put_back_when_the_window_moved()
    {
        var w = new FakeLayerWindow();
        var placement = w.Placement();
        var game = new PixelRect(0, 0, 1920, 1080);
        placement.Place(Hwnd, game);
        Assert.False(placement.Place(Hwnd, game)); // unchanged: no re-layout
        Assert.Equal(1, w.Sets); // and no SetWindowPos when it is already right

        w.Bounds = new PixelRect(0, 0, 1280, 720); // WM_DPICHANGED resized it behind our back
        placement.Reapply(Hwnd);
        Assert.Equal(game, w.Bounds);
        Assert.Single(w.Log); // one line saying what it was
    }

    [Fact]
    public void A_rect_that_will_not_stick_is_logged_once()
    {
        var w = new FakeLayerWindow { Override = r => r with { Height = r.Height - 40 } };
        var placement = w.Placement();
        var game = new PixelRect(0, 0, 1920, 1080);
        placement.Place(Hwnd, game);
        placement.Place(Hwnd, game);
        placement.Place(Hwnd, game);
        var line = Assert.Single(w.Log);
        Assert.Contains("intended", line);
        Assert.Contains("actual", line);
    }
}
