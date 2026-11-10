namespace Sts2Overlay.App.Integration;

/// <summary>
/// The panel's 추천 tab offer, written one dispatcher pass AFTER the badges. WPF lays out and renders every window of
/// the app on one UI thread, so when a new reward / shop screen sets the badge layer and the panel's offer rows in the
/// same pass, the badges wait for the panel's rows (20-70 ms for 3-7 rows). <see cref="Defer"/> keeps the new offer
/// here and posts the panel write below Render priority, so the badge frame is laid out and handed to the render
/// thread first; the panel follows one pass later.
/// <para>Stale guard: only the newest value is ever written. A later <see cref="Defer"/> replaces the pending one (the
/// earlier callback finds a newer version and does nothing), and <see cref="Set"/> (clearing, stale-marking, an event's
/// options) writes at once and drops whatever was pending, so an old offer can never land after a newer state.</para>
/// <para>UI-free (unit-tested): the controller passes the view-model read / write and the dispatcher post.</para>
/// </summary>
/// <param name="read">The value the panel shows now.</param>
/// <param name="write">Writes a value to the panel; the flag says to switch to the 추천 tab as well.</param>
/// <param name="post">Runs an action later on the same thread (the controller: Dispatcher.BeginInvoke at Background).</param>
public sealed class DeferredOffer<T>(Func<T> read, Action<T, bool> write, Action<Action> post) where T : class
{
    private T? _pending;
    private bool _pendingSelect;
    private int _version;

    /// <summary>What the panel will show: the pending value, else the one it shows now. Read this, not the view model.</summary>
    public T Current => _pending ?? read();

    /// <summary>A deferred value is waiting for its pass.</summary>
    public bool IsPending => _pending is not null;

    /// <summary>
    /// Shows <paramref name="value"/> one dispatcher pass later. <paramref name="selectTab"/> = switch to the 추천 tab
    /// then; a tab switch asked for by a value this one replaces is kept (the screen that asked for it did open).
    /// </summary>
    public void Defer(T value, bool selectTab)
    {
        _pending = value;
        _pendingSelect |= selectTab;
        var version = ++_version;
        post(() =>
        {
            if (version != _version || _pending is not { } pending) return; // replaced or written since
            Write(pending);
        });
    }

    /// <summary>Writes <paramref name="value"/> now and drops any pending value (its tab switch still happens).</summary>
    public void Set(T value)
    {
        _version++;
        Write(value);
    }

    /// <summary>Writes the pending value now, if any (tests; a caller that must see the panel up to date).</summary>
    public void Flush()
    {
        if (_pending is { } pending)
        {
            _version++;
            Write(pending);
        }
    }

    private void Write(T value)
    {
        var select = _pendingSelect;
        _pending = null;
        _pendingSelect = false;
        write(value, select);
    }
}
