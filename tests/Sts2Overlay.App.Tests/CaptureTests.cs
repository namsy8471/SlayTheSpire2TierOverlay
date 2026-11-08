using System.Runtime.InteropServices;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.Tests;

public class PixelCopyTests
{
    /// <summary>Image of <paramref name="rows"/> rows, <paramref name="rowBytes"/> payload bytes each, padded to <paramref name="pitch"/> with 0xEE.</summary>
    private static byte[] Padded(int rowBytes, int pitch, int rows)
    {
        var a = new byte[pitch * rows];
        Array.Fill(a, (byte)0xEE);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < rowBytes; x++) a[y * pitch + x] = (byte)(y * 31 + x);
        }
        return a;
    }

    [Fact]
    public void Tightly_packed_rows_copy_as_one_block()
    {
        var src = Padded(12, 12, 5);
        var dst = new byte[60];
        PixelCopy.CopyRows(src, 12, dst, 12, 12, 5);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Row_pitch_padding_is_skipped()
    {
        // 3 px wide = 12 bytes per row, but the "GPU" pads rows to 16 bytes.
        var src = Padded(12, 16, 4);
        var dst = new byte[12 * 4];
        PixelCopy.CopyRows(src, 16, dst, 12, 12, 4);
        Assert.DoesNotContain((byte)0xEE, dst);
        for (var y = 0; y < 4; y++)
        {
            Assert.Equal(src.AsSpan(y * 16, 12).ToArray(), dst.AsSpan(y * 12, 12).ToArray());
        }
    }

    [Fact]
    public void Destination_stride_larger_than_row_leaves_padding_untouched()
    {
        var src = Padded(8, 8, 3);
        var dst = new byte[10 * 3];
        Array.Fill(dst, (byte)0x55);
        PixelCopy.CopyRows(src, 8, dst, 10, 8, 3);
        Assert.Equal((byte)0x55, dst[8]);
        Assert.Equal((byte)0x55, dst[9]);
        Assert.Equal(src.AsSpan(16, 8).ToArray(), dst.AsSpan(20, 8).ToArray());
    }

    [Fact]
    public void Last_row_needs_no_trailing_padding()
    {
        // Mapped memory may end right after the last pixel: (rows - 1) * pitch + rowBytes.
        var src = Padded(12, 16, 3)[..(2 * 16 + 12)];
        var dst = new byte[36];
        PixelCopy.CopyRows(src, 16, dst, 12, 12, 3);
        Assert.Equal(src.AsSpan(32, 12).ToArray(), dst.AsSpan(24, 12).ToArray());
    }

    [Fact]
    public void Pointer_overload_reads_unmanaged_memory()
    {
        var src = Padded(12, 16, 4);
        var dst = new byte[48];
        var handle = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            PixelCopy.CopyRows(handle.AddrOfPinnedObject(), 16, dst, 12, 12, 4);
        }
        finally
        {
            handle.Free();
        }
        Assert.Equal(src.AsSpan(48, 12).ToArray(), dst.AsSpan(36, 12).ToArray());
    }

    [Fact]
    public void Bad_arguments_throw()
    {
        var buf = new byte[64];
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelCopy.CopyRows(buf, 8, buf.ToArray(), 16, 12, 2)); // pitch < row
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelCopy.CopyRows(buf, 16, new byte[64], 8, 12, 2)); // stride < row
        Assert.Throws<ArgumentException>(() => PixelCopy.CopyRows(new byte[20], 16, new byte[64], 16, 12, 2)); // source short
        Assert.Throws<ArgumentException>(() => PixelCopy.CopyRows(buf, 16, new byte[20], 16, 12, 2)); // destination short
    }
}

public class CaptureBackendSelectorTests
{
    [Theory]
    [InlineData(null, true, CaptureBackend.Wgc)]
    [InlineData("", true, CaptureBackend.Wgc)]
    [InlineData("auto", true, CaptureBackend.Wgc)]
    [InlineData("WGC", true, CaptureBackend.Wgc)]
    [InlineData(" gdi ", true, CaptureBackend.Gdi)]
    [InlineData("Gdi", false, CaptureBackend.Gdi)]
    [InlineData("auto", false, CaptureBackend.Gdi)]
    [InlineData("wgc", false, CaptureBackend.Gdi)]
    [InlineData("dxgi", true, CaptureBackend.Wgc)]
    [InlineData("dxgi", false, CaptureBackend.Gdi)]
    public void Chooses_backend_from_setting_and_support(string? setting, bool wgcSupported, CaptureBackend expected)
    {
        var (backend, reason) = CaptureBackendSelector.Choose(setting, wgcSupported);
        Assert.Equal(expected, backend);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Unknown_setting_is_mentioned_in_the_reason()
        => Assert.Contains("unknown captureBackend 'dxgi'", CaptureBackendSelector.Choose("dxgi", true).Reason);

    [Fact]
    public void Settings_default_is_auto() => Assert.Equal("auto", new AppSettings().CaptureBackend);
}

public class FallbackFrameCapturerTests
{
    private static readonly GameWindowInfo Target = new(IntPtr.Zero, 0, new PixelRect(0, 0, 2, 1), new PixelRect(0, 0, 2, 1), false, true, false, false);

    private sealed class FakeCapturer(string name, bool readsDesktop = false) : IFrameCapturer
    {
        public bool ReadsDesktop(GameWindowInfo target) => readsDesktop;

        public Queue<bool> Script { get; } = new(); // true = succeed, false = throw; empty = succeed
        public int Calls { get; private set; }
        public int Suspends { get; private set; }
        public bool Disposed { get; private set; }
        public string Name => name;
        public CaptureTimings LastTimings => new(1, 2);

        public CapturedFrame Capture(GameWindowInfo target)
        {
            Calls++;
            if (Script.Count > 0 && !Script.Dequeue()) throw new InvalidOperationException(name + " broke");
            return new CapturedFrame([(byte)name[0], 0, 0, 0, 0, 0, 0, 0], 2, 1, 8, target.CaptureRect, DateTime.Now);
        }

        public void Suspend() => Suspends++;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Uses_primary_while_it_works()
    {
        var wgc = new FakeCapturer("wgc");
        var gdi = new FakeCapturer("gdi");
        using var c = new FallbackFrameCapturer(wgc, gdi);
        Assert.Equal((byte)'w', c.Capture(Target).Bgra[0]);
        Assert.Equal("wgc", c.Name);
        Assert.Equal(0, gdi.Calls);
    }

    [Fact]
    public void One_failure_falls_back_for_that_frame_only()
    {
        var wgc = new FakeCapturer("wgc");
        var gdi = new FakeCapturer("gdi");
        wgc.Script.Enqueue(false);
        using var c = new FallbackFrameCapturer(wgc, gdi);
        Assert.Equal((byte)'g', c.Capture(Target).Bgra[0]);
        Assert.Equal("gdi", c.Name);
        Assert.Equal((byte)'w', c.Capture(Target).Bgra[0]);
        Assert.Equal("wgc", c.Name);
        Assert.False(c.UsingFallbackOnly);
    }

    [Fact]
    public void Consecutive_failures_disable_the_primary_for_good()
    {
        var wgc = new FakeCapturer("wgc");
        var gdi = new FakeCapturer("gdi");
        for (var i = 0; i < FallbackFrameCapturer.MaxConsecutiveFailures; i++) wgc.Script.Enqueue(false);
        using var c = new FallbackFrameCapturer(wgc, gdi);
        for (var i = 0; i < FallbackFrameCapturer.MaxConsecutiveFailures + 2; i++)
        {
            Assert.Equal((byte)'g', c.Capture(Target).Bgra[0]);
        }
        Assert.Equal(FallbackFrameCapturer.MaxConsecutiveFailures, wgc.Calls);
        Assert.True(wgc.Disposed);
        Assert.True(c.UsingFallbackOnly);
    }

    [Fact]
    public void A_success_resets_the_failure_count()
    {
        var wgc = new FakeCapturer("wgc");
        var gdi = new FakeCapturer("gdi");
        foreach (var ok in new[] { false, false, true, false, false, true }) wgc.Script.Enqueue(ok);
        using var c = new FallbackFrameCapturer(wgc, gdi);
        for (var i = 0; i < 6; i++) c.Capture(Target);
        Assert.False(c.UsingFallbackOnly);
        Assert.Equal(4, gdi.Calls);
    }

    [Fact]
    public void No_primary_means_fallback_only()
    {
        var gdi = new FakeCapturer("gdi");
        using var c = new FallbackFrameCapturer(null, gdi);
        Assert.True(c.UsingFallbackOnly);
        Assert.Equal("gdi", c.Name);
        Assert.Equal((byte)'g', c.Capture(Target).Bgra[0]);
    }

    [Fact]
    public void Fallback_failure_propagates()
    {
        var wgc = new FakeCapturer("wgc");
        var gdi = new FakeCapturer("gdi");
        wgc.Script.Enqueue(false);
        gdi.Script.Enqueue(false);
        using var c = new FallbackFrameCapturer(wgc, gdi);
        Assert.Throws<InvalidOperationException>(() => c.Capture(Target));
    }

    private sealed class LoggingCapturer(string name, bool readsDesktop, List<string> log, bool fail = false) : IFrameCapturer
    {
        public string Name => name;
        public CaptureTimings LastTimings => default;

        public bool ReadsDesktop(GameWindowInfo target) => readsDesktop;

        public CapturedFrame Capture(GameWindowInfo target)
        {
            log.Add("capture " + name);
            if (fail) throw new InvalidOperationException(name + " broke");
            return new CapturedFrame(new byte[8], 2, 1, 8, target.CaptureRect, DateTime.Now);
        }

        public void Suspend()
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Before_capture_runs_first_and_says_whether_the_backend_reads_the_desktop()
    {
        var log = new List<string>();
        using var c = new FallbackFrameCapturer(new LoggingCapturer("wgc", false, log), new LoggingCapturer("gdi", true, log))
        {
            BeforeCapture = desktop => log.Add("before " + (desktop ? "desktop" : "window")),
        };
        c.Capture(Target);
        Assert.Equal(["before window", "capture wgc"], log);
    }

    [Fact]
    public void A_failed_window_capture_hides_the_overlay_before_the_gdi_frame()
    {
        var log = new List<string>();
        using var c = new FallbackFrameCapturer(new LoggingCapturer("wgc", false, log, fail: true), new LoggingCapturer("gdi", true, log))
        {
            BeforeCapture = desktop => log.Add("before " + (desktop ? "desktop" : "window")),
        };
        c.Capture(Target);
        Assert.Equal(["before window", "capture wgc", "before desktop", "capture gdi"], log);
    }

    [Fact]
    public void Reads_desktop_follows_the_backend_in_use()
    {
        var log = new List<string>();
        using var withPrimary = new FallbackFrameCapturer(new LoggingCapturer("wgc", false, log), new LoggingCapturer("gdi", true, log));
        Assert.False(withPrimary.ReadsDesktop(Target));
        using var gdiOnly = new FallbackFrameCapturer(null, new LoggingCapturer("gdi", true, log));
        Assert.True(gdiOnly.ReadsDesktop(Target));
    }

    [Fact]
    public void Suspend_and_dispose_reach_both_backends()
    {
        var wgc = new FakeCapturer("wgc");
        var gdi = new FakeCapturer("gdi");
        var c = new FallbackFrameCapturer(wgc, gdi);
        c.Suspend();
        c.Dispose();
        Assert.Equal(1, wgc.Suspends);
        Assert.Equal(1, gdi.Suspends);
        Assert.True(wgc.Disposed && gdi.Disposed);
    }
}

public class FrameCompareTests
{
    private static byte[] Noise(int w, int h, int seed)
    {
        var a = new byte[w * h * 4];
        new Random(seed).NextBytes(a);
        return a;
    }

    [Fact]
    public void Identical_frames_have_no_mismatch_and_alpha_is_ignored()
    {
        var a = Noise(17, 9, 1);
        var b = (byte[])a.Clone();
        for (var i = 3; i < b.Length; i += 4) b[i] ^= 0xFF; // GDI alpha 0 vs WGC alpha 255
        var s = FrameCompare.Compare(a, 68, b, 68, 17, 9);
        Assert.Equal(17 * 9, s.Compared);
        Assert.Equal(0, s.Mismatched);
    }

    [Fact]
    public void Mismatch_count_max_and_bounds()
    {
        var a = Noise(10, 10, 2);
        var b = (byte[])a.Clone();
        b[(3 * 10 + 4) * 4 + 1] ^= 0x10; // (4, 3) green
        b[(7 * 10 + 8) * 4 + 2] = (byte)(b[(7 * 10 + 8) * 4 + 2] ^ 0xFF); // (8, 7) red
        var s = FrameCompare.Compare(a, 40, b, 40, 10, 10);
        Assert.Equal(2, s.Mismatched);
        Assert.Equal(new PixelRect(4, 3, 5, 5), s.MismatchBounds);
        Assert.True(s.MaxDiff >= 16);
    }

    [Fact]
    public void Masked_compare_skips_changed_and_excluded_pixels()
    {
        const int w = 8, h = 4;
        var reference = Noise(w, h, 3);
        var later = (byte[])reference.Clone();
        later[(1 * w + 1) * 4] ^= 1; // (1, 1) animates
        var stable = new bool[w * h];
        Array.Fill(stable, true);
        FrameCompare.ClearChanged(stable, reference, later, w, h, w * 4);
        Assert.False(stable[1 * w + 1]);

        var test = (byte[])reference.Clone();
        test[(1 * w + 1) * 4] ^= 0x80; // differs only where the content was animating: not counted
        test[(2 * w + 6) * 4] ^= 0x80; // differs inside an excluded window: not counted
        var s = FrameCompare.CompareMasked(test, reference, stable, w, h, w * 4, [new PixelRect(6, 2, 2, 2)]);
        Assert.Equal(0, s.Mismatched);
        Assert.Equal(w * h - 1 - 4, s.Compared);

        test[0] ^= 1;
        Assert.Equal(1, FrameCompare.CompareMasked(test, reference, stable, w, h, w * 4, [new PixelRect(6, 2, 2, 2)]).Mismatched);
        // Without the exclusion the pixel under the "other window" counts too.
        Assert.Equal(2, FrameCompare.CompareMasked(test, reference, stable, w, h, w * 4, []).Mismatched);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(-2, 1)]
    public void Best_offset_finds_a_shifted_crop(int dx, int dy)
    {
        const int w = 120, h = 80;
        var reference = Noise(w, h, 4);
        // test(x + dx, y + dy) == reference(x, y): the test frame is the reference moved by (dx, dy).
        var test = new byte[reference.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Clamp(x - dx, 0, w - 1);
                var sy = Math.Clamp(y - dy, 0, h - 1);
                reference.AsSpan((sy * w + sx) * 4, 4).CopyTo(test.AsSpan((y * w + x) * 4));
            }
        }
        var stable = new bool[w * h];
        Array.Fill(stable, true);
        var (bx, by, _, best) = FrameCompare.BestOffset(test, reference, stable, w, h, w * 4, 2);
        Assert.Equal((dx, dy), (bx, by));
        Assert.Equal(0, best);
    }
}

public class CaptureOptionsTests
{
    [Fact]
    public void Bench_and_verify_flags_parse()
    {
        var o = AppOptions.Parse(["--bench-capture", "25", "--bench-target", "notepad", "--bench-out", "x", "--bench-soak", "5"]);
        Assert.True(o.BenchCapture);
        Assert.Equal(25, o.BenchFrames);
        Assert.Equal("notepad", o.BenchTarget);
        Assert.Equal("x", o.BenchOut);
        Assert.Equal(5, o.BenchSoakSeconds);

        var v = AppOptions.Parse(["--verify-capture", "--verify-dir", "d"]);
        Assert.True(v.VerifyCapture);
        Assert.Equal(12, v.VerifyCount);
        Assert.Equal("d", v.VerifyDir);
        Assert.False(v.VerifyGdiOnTop);
    }
}

public class OverlayCaptureVisibilityTests
{
    [Theory]
    [InlineData(true, false, false)] // normal play: window capture, user wants the overlay in OBS → visible
    [InlineData(true, true, true)] // GDI / monitor capture: hidden, or the badges would be in our own frames
    [InlineData(false, false, true)] // user turned it off
    [InlineData(false, true, true)]
    public void Policy(bool showInCapture, bool recognitionSeesDesktop, bool excluded)
        => Assert.Equal(excluded, CaptureExclusionPolicy.ShouldExclude(showInCapture, recognitionSeesDesktop));

    [Fact]
    public void Visible_by_default_and_hidden_while_recognition_reads_the_desktop()
    {
        var v = new OverlayCaptureVisibility(showInCapture: true);
        var changes = new List<bool>();
        v.ExcludedChanged += changes.Add;
        Assert.False(v.IsExcluded);

        v.BeforeCapture(false); // window capture: nothing to do
        Assert.False(v.IsExcluded);

        v.BeforeCapture(true); // WGC failed, GDI frame next
        Assert.True(v.IsExcluded);
        Assert.True(v.RecognitionSeesDesktop);

        v.BeforeCapture(true);
        v.BeforeCapture(false); // WGC works again
        Assert.False(v.IsExcluded);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void The_user_can_hide_it_and_a_desktop_read_never_shows_it()
    {
        var v = new OverlayCaptureVisibility(showInCapture: false);
        Assert.True(v.IsExcluded);
        v.BeforeCapture(true);
        v.ShowInCapture = true; // turned on while GDI is in use: stays hidden
        Assert.True(v.IsExcluded);
        v.BeforeCapture(false);
        Assert.False(v.IsExcluded);
        v.ShowInCapture = false;
        Assert.True(v.IsExcluded);
    }

    [Fact]
    public void Settings_default_shows_the_overlay_in_capture() => Assert.True(new AppSettings().ShowOverlayInCapture);
}
