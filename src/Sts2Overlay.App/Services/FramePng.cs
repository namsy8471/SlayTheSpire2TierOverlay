using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.Services;

/// <summary>Writes a <see cref="CapturedFrame"/> as PNG (debug captures, capture benchmark).</summary>
internal static class FramePng
{
    public static void Save(CapturedFrame frame, string file)
    {
        // Bgr32 = BGRA memory layout with the alpha byte ignored (GDI leaves it 0, WGC 255).
        var bmp = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Bgra, frame.Stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var fs = File.Create(file);
        encoder.Save(fs);
    }
}
