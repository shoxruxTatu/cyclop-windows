namespace Cyclop;

/// <summary>
/// Runs the latest scheduled write a moment after the last request, on the
/// thread that asked — the UI thread in the app, so the list being written is
/// never the list being edited. Without a synchronization context (tests) it
/// runs on the timer's thread.
/// </summary>
public sealed class DebouncedWrite
{
    readonly TimeSpan delay;
    readonly object gate = new();
    Timer? timer;
    Action? pending;
    SynchronizationContext? context;

    public DebouncedWrite(double seconds = 0.6) => delay = TimeSpan.FromSeconds(seconds);

    public void Schedule(Action write)
    {
        lock (gate)
        {
            pending = write;
            context = SynchronizationContext.Current;
            timer ??= new Timer(_ => Fire());
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire()
    {
        var ctx = context;
        if (ctx is not null) ctx.Post(_ => Flush(), null);
        else Flush();
    }

    /// <summary>Writes now whatever is waiting — on quit, or on leaving a tab.</summary>
    public void Flush()
    {
        Action? write;
        lock (gate)
        {
            write = pending;
            pending = null;
            timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        write?.Invoke();
    }

    public void Cancel()
    {
        lock (gate) pending = null;
    }
}

/// <summary>
/// A repeating callback delivered on the thread that started it. The stores
/// that refresh on a clock — rates every hour, calendar feeds every quarter
/// hour, the meeting countdown every half minute — use this rather than a
/// UI timer so they stay testable without one.
/// </summary>
public sealed class Repeating : IDisposable
{
    Timer? timer;

    public Repeating(TimeSpan interval, Action tick, bool fireNow = false)
    {
        var ctx = SynchronizationContext.Current;
        timer = new Timer(_ =>
        {
            if (ctx is not null) ctx.Post(_ => { if (timer is not null) tick(); }, null);
            else tick();
        }, null, fireNow ? TimeSpan.Zero : interval, interval);
    }

    public void Dispose()
    {
        timer?.Dispose();
        timer = null;
    }
}
