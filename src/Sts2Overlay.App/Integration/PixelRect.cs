namespace Sts2Overlay.App.Integration;

/// <summary>
/// A rectangle in PHYSICAL screen pixels (what Win32 and the captured frame use).
/// Deliberately a different type from WPF's <c>System.Windows.Rect</c> (which the UI uses in DIPs), so the compiler
/// stops us from mixing the two units by accident. Convert with <see cref="Interop.Dpi"/> at the UI boundary.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromLtrb(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);

    public PixelRect Intersect(PixelRect other)
    {
        var l = Math.Max(X, other.X);
        var t = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r > l && b > t ? FromLtrb(l, t, r, b) : default;
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public override string ToString() => $"{Width}x{Height}@{X},{Y}";
}
