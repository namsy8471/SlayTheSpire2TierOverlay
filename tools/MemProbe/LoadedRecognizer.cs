using System.Globalization;
using System.IO;
using Sts2Overlay.App.Integration;

namespace MemProbe;

/// <summary>
/// The app's own <see cref="VisionRecognizer"/>, loaded from <c>local</c> but with its art cache in a temp folder: the
/// shared cache is COPIED there first (cold = not copied, so the database is built), so nothing is ever written into
/// data/local even when the cache turns out stale.
/// </summary>
internal sealed class LoadedRecognizer : IDisposable
{
    private readonly string _tempDir;

    public VisionRecognizer Recognizer { get; }

    private LoadedRecognizer(string tempDir, VisionRecognizer recognizer)
    {
        _tempDir = tempDir;
        Recognizer = recognizer;
    }

    public static LoadedRecognizer Create(string local, bool cold)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MemProbe", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(tempDir);
        string cache = Path.Combine(tempDir, "card-art.bin");
        string shared = Path.Combine(local, "vision", "card-art.bin");
        if (!cold && File.Exists(shared)) File.Copy(shared, cache, overwrite: true);
        var recognizer = new VisionRecognizer(local, cache);
        if (!recognizer.Preload()) throw new InvalidOperationException("recogniser did not load (no card art?)");
        return new LoadedRecognizer(tempDir, recognizer);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a temp folder
        }
    }
}
