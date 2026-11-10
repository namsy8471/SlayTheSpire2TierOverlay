namespace Sts2Overlay.GameState.Tracking;

/// <summary>
/// "Say the same thing at most once per <c>window</c>": a log line the parser chokes on comes back on every poll
/// (and again after every restart), so without this one bad line could fill app.log. Thread-safe.
/// </summary>
internal sealed class ReportThrottle(TimeSpan window)
{
    private const int MaxKeys = 256; // distinct messages remembered; more than this and the memory is simply reset
    private readonly Dictionary<string, DateTime> _lastSaid = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>True if <paramref name="key"/> was not reported in the last window (and records it as reported now).</summary>
    public bool ShouldReport(string key, DateTime now)
    {
        lock (_gate)
        {
            if (_lastSaid.TryGetValue(key, out DateTime at) && now - at < window) return false;
            if (_lastSaid.Count >= MaxKeys) _lastSaid.Clear();
            _lastSaid[key] = now;
            return true;
        }
    }
}
