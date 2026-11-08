namespace Sts2Overlay.App.Integration;

/// <summary>
/// One screen grab of the game client area: 32-bit BGRA, top-down rows (row 0 = top).
/// The alpha byte is undefined (GDI usually leaves 0): ignore it.
/// Pixel (x, y) starts at <c>Bgra[y * Stride + x * 4]</c> as B, G, R, A.
/// </summary>
/// <remarks>
/// To keep allocations low the capture service REUSES <see cref="Bgra"/> for the next frame (a 4K frame is 33 MB;
/// allocating that 3x per second would churn the large-object heap). A frame is only valid until the next capture,
/// i.e. for the duration of <see cref="ICardRecognizer.Recognize"/>.
/// </remarks>
public sealed class CapturedFrame(byte[] bgra, int width, int height, int stride, PixelRect screenRect, DateTime at)
{
    public byte[] Bgra { get; } = bgra;
    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>Bytes per row (= Width * 4 for our captures).</summary>
    public int Stride { get; } = stride;

    /// <summary>Where the frame's (0, 0) sits on screen, physical pixels. Add X/Y to frame coordinates to get screen coordinates.</summary>
    public PixelRect ScreenRect { get; } = screenRect;

    public DateTime At { get; } = at;
}
