namespace SerialScout.Core.Sessions;

/// <summary>
/// Direction of one captured log event.
/// </summary>
public enum LogEventDirection
{
    /// <summary>Bytes received from the device.</summary>
    Received,

    /// <summary>Bytes sent to the device (already framed with the session line ending).</summary>
    Sent,
}

/// <summary>
/// One timestamped, bounded chunk of session traffic. Events carry raw bytes rather
/// than decoded text: partial UTF-8 sequences across chunk boundaries are expected on
/// live links, so decoding stays a presentation concern (issue #5).
/// </summary>
/// <param name="Utc">UTC instant the bytes were observed.</param>
/// <param name="Direction">Whether the bytes were received or sent.</param>
/// <param name="Payload">The byte chunk; never empty.</param>
public sealed record LogEvent(DateTimeOffset Utc, LogEventDirection Direction, byte[] Payload)
{
    /// <summary>Total bytes carried by this event (used by bounded retention).</summary>
    public int ByteCount => Payload.Length;
}

/// <summary>
/// Thread-safe in-memory rolling capture with a hard byte budget. Oldest events are
/// evicted first; if a single incoming event exceeds the budget the buffer collapses to
/// just that event. Insertion is O(1); snapshots are O(retained events).
/// </summary>
public sealed class RollingSessionLog
{
    private readonly object _gate = new();
    private readonly LinkedList<LogEvent> _events = new();
    private long _totalBytes;

    /// <summary>Creates a log retaining at most <paramref name="maxBytes"/> payload bytes.</summary>
    /// <param name="maxBytes">Hard retention budget in payload bytes; must be positive.</param>
    public RollingSessionLog(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        MaxBytes = maxBytes;
    }

    /// <summary>Hard retention budget in payload bytes.</summary>
    public int MaxBytes { get; }

    /// <summary>Payload bytes currently retained.</summary>
    public long RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>Number of events currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>
    /// Appends one event, evicting oldest events until the budget holds. Events with an
    /// empty payload are ignored so callers cannot create budget-free noise.
    /// </summary>
    public void Append(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (logEvent.ByteCount == 0)
        {
            return;
        }

        lock (_gate)
        {
            _events.AddLast(logEvent);
            _totalBytes += logEvent.ByteCount;
            while (_totalBytes > MaxBytes && _events.Count > 1)
            {
                var oldest = _events.First!;
                _events.RemoveFirst();
                _totalBytes -= oldest.Value.ByteCount;
            }
        }
    }

    /// <summary>Snapshot of retained events, oldest first.</summary>
    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }

    /// <summary>Drops every retained event and resets the byte counter.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
            _totalBytes = 0;
        }
    }
}
