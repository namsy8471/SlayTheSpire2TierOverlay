namespace Sts2Overlay.App.Services;

/// <summary>
/// Copies an image whose rows are <c>srcPitch</c> bytes apart into a buffer whose rows are <c>dstStride</c> bytes apart.
///
/// Why rows can be further apart than <c>width * 4</c>: GPUs and drivers like rows to start on aligned addresses
/// (often 64 or 256 bytes), so a mapped texture's <c>RowPitch</c> may include padding after the last pixel of each row.
/// Copying the whole block in one go would then smear that padding into the image and shift every following row.
/// When both sides happen to be tightly packed we do one big copy instead of one per row.
/// </summary>
internal static unsafe class PixelCopy
{
    public static void CopyRows(ReadOnlySpan<byte> src, int srcPitch, Span<byte> dst, int dstStride, int rowBytes, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        if (rows == 0 || rowBytes == 0) return;
        ArgumentOutOfRangeException.ThrowIfNegative(rowBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(srcPitch, rowBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(dstStride, rowBytes);
        // The last row only needs rowBytes, not a full pitch (the padding after it may not be mapped).
        if (src.Length < RequiredLength(srcPitch, rowBytes, rows)) throw new ArgumentException("source too small", nameof(src));
        if (dst.Length < RequiredLength(dstStride, rowBytes, rows)) throw new ArgumentException("destination too small", nameof(dst));

        if (srcPitch == rowBytes && dstStride == rowBytes)
        {
            src[..(rowBytes * rows)].CopyTo(dst);
            return;
        }
        for (var y = 0; y < rows; y++)
        {
            src.Slice(y * srcPitch, rowBytes).CopyTo(dst.Slice(y * dstStride, rowBytes));
        }
    }

    /// <summary>Pointer overload for mapped GPU memory.</summary>
    public static void CopyRows(IntPtr src, int srcPitch, Span<byte> dst, int dstStride, int rowBytes, int rows)
    {
        if (rows <= 0 || rowBytes <= 0) return;
        var length = RequiredLength(srcPitch, rowBytes, rows);
        CopyRows(new ReadOnlySpan<byte>((void*)src, checked((int)length)), srcPitch, dst, dstStride, rowBytes, rows);
    }

    private static long RequiredLength(int pitch, int rowBytes, int rows) => (long)(rows - 1) * pitch + rowBytes;
}
