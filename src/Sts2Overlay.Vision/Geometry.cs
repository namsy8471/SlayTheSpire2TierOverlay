namespace Sts2Overlay.Vision;

/// <summary>Axis-aligned rectangle with float coordinates (used for the 1920x1080 reference space).</summary>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CenterX => X + Width / 2;
    public float CenterY => Y + Height / 2;

    public static RectF FromCenter(float cx, float cy, float w, float h) => new(cx - w / 2, cy - h / 2, w, h);

    public RectF Offset(float dx, float dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>Sub-rectangle given as fractions of this rectangle (0..1).</summary>
    public RectF Fraction(float fx, float fy, float fw, float fh) => new(X + fx * Width, Y + fy * Height, fw * Width, fh * Height);

    public override string ToString() => $"({X:F1},{Y:F1} {Width:F1}x{Height:F1})";
}

/// <summary>Integer rectangle in frame (screen) pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public PixelRect Intersect(int width, int height)
    {
        int x0 = Math.Clamp(X, 0, width), y0 = Math.Clamp(Y, 0, height);
        int x1 = Math.Clamp(Right, 0, width), y1 = Math.Clamp(Bottom, 0, height);
        return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}

/// <summary>
/// Maps the game's 1920x1080 design coordinates ("reference" space, the numbers you read in the decompiled
/// scenes) to pixels of a captured frame.
///
/// The game runs Godot with stretch mode <c>canvas_items</c> and aspect <c>expand</c> (project.binary):
/// the UI is scaled uniformly by <c>s = min(W/1920, H/1080)</c> and the extra space on the longer axis is
/// added symmetrically. Every screen we read (card row, shop slots) is anchored to the screen centre, so
/// <c>pixel = frameCentre + (ref - refCentre) * s</c>. The same formula also covers letterboxed captures
/// (black bars) and the user's 3842x2160 window (s = 2, 1 px extra on each side).
/// </summary>
public readonly record struct RefTransform(float Scale, float OffsetX, float OffsetY)
{
    public const float RefWidth = 1920f, RefHeight = 1080f;

    public static RefTransform For(int frameWidth, int frameHeight)
    {
        float s = Math.Min(frameWidth / RefWidth, frameHeight / RefHeight);
        return new RefTransform(s, (frameWidth - RefWidth * s) / 2f, (frameHeight - RefHeight * s) / 2f);
    }

    public float ToPixelX(float refX) => OffsetX + refX * Scale;
    public float ToPixelY(float refY) => OffsetY + refY * Scale;

    public RectF ToPixels(RectF r) => new(ToPixelX(r.X), ToPixelY(r.Y), r.Width * Scale, r.Height * Scale);

    public PixelRect ToPixelRect(RectF r)
    {
        RectF p = ToPixels(r);
        int x0 = (int)MathF.Floor(p.X), y0 = (int)MathF.Floor(p.Y);
        int x1 = (int)MathF.Ceiling(p.Right), y1 = (int)MathF.Ceiling(p.Bottom);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    public RectF ToRef(RectF pixels) =>
        new((pixels.X - OffsetX) / Scale, (pixels.Y - OffsetY) / Scale, pixels.Width / Scale, pixels.Height / Scale);
}
