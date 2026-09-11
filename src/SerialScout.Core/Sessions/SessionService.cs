using System.Text;
using System.Threading.Channels;
using SerialScout.Core.Profiles;
using SerialScout.Core.Storage;

namespace SerialScout.Core.Sessions;

/// <summary>
/// Live state of a <see cref="SessionService"/>.
/// </summary>
public enum SessionState
{
    /// <summary>The link is open and monitored.</summary>
    Connected,

    /// <summary>The link dropped; automatic reconnect attempts are in progress.</summary>
    Reconnecting,

    /// <summary>The session stopped cleanly (user request).</summary>
    Stopped,

    /// <summary>The session failed permanently (open failure or abandoned reconnect).</summary>
    Failed,
}

/// <summary>
/// Owns exactly one serial session: opens the port, streams incoming bytes into a
/// bounded <see cref="RollingSessionLog"/>, sends framed text/raw frames, records send
/// history, reconnects automatically after unexpected drops, and emits structured
/// <see cref="SessionEvent"/> lifecycle transitions for the UI layer.
/// All OS interaction goes through <see cref="ISerialLinkFactory"/> so the engine is
/// fully testable without hardware. Everything stays local: no network I/O anywhere.
/// </summary>
public sealed class SessionService : IAsyncDisposable
{
    private const int TerminalNone = 0;
    private const int TerminalStopped = 1;
    private const int TerminalFailed = 2;

    private readonly SessionOptions _options;
    private readonly ISerialLinkFactory _factory;
    private readonly ProfileStore? _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private ISerialLink? _link;
    private volatile bool _linkUsable;
    private CancellationTokenSource _linkAbort = new();
    private string? _pendingDropReason;
    private string? _pendingDropDetail;
    private Task? _monitor;
    private long _sessionRowId;
    private int _terminal;
    private int _primitivesDisposed;
    private int _state;

    private SessionService(
        SessionOptions options,
        ISerialLinkFactory factory,
        ProfileStore? store,
        Func<DateTimeOffset> utcNow)
    {
        _options = options;
        _factory = factory;
        _store = store;
        _utcNow = utcNow;
        Log = new RollingSessionLog(options.LogMaxBytes);
        History = new SendHistory(options.HistoryCapacity);
    }

    /// <summary>Bounded rolling capture of session traffic, oldest first.</summary>
    public RollingSessionLog Log { get; }

    /// <summary>Most-recently-sent text frames, scoped by port path.</summary>
    public SendHistory History { get; }

    /// <summary>Stream of structured lifecycle events. Buffered; never drops events.</summary>
    public ChannelReader<SessionEvent> Events => _events.Reader;

    /// <summary>Current live state.</summary>
    public SessionState State => (SessionState)Volatile.Read(ref _state);

    /// <summary>
    /// Opens the port described by <paramref name="options"/> and starts the monitor
    /// loop. The initial connect happens synchronously (awaited) so callers learn the
    /// outcome immediately; subsequent drops are handled by the background loop.
    /// When <paramref name="store"/> is provided, a <see cref="SessionMetadata"/> row is
    /// created for the session lifetime.
    /// </summary>
    public static Task<SessionStartResult> StartAsync(
        SessionOptions options,
        ISerialLinkFactory factory,
        ProfileStore? store = null,
        Func<DateTimeOffset>? utcNow = null,
        CancellationToken cancellationToken = default) =>
        TryStartAsync(options, factory, store, utcNow, cancellationToken);

    /// <summary>
    /// Same as <see cref="StartAsync"/> but returns an error result instead of throwing
    /// when the port cannot be opened. A <see cref="SessionEventType.Failed"/> event
    /// with reason <see cref="SessionEventReasons.OpenFailed"/> is always emitted for a
    /// failed attempt, and no primitives leak either way.
    /// </summary>
    public static async Task<SessionStartResult> TryStartAsync(
        SessionOptions options,
        ISerialLinkFactory factory,
        ProfileStore? store = null,
        Func<DateTimeOffset>? utcNow = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        options.Validate();

        var service = new SessionService(options, factory, store, utcNow ?? (static () => DateTimeOffset.UtcNow));
        var link = factory.Create(options.PortPath);
        try
        {
            await link.OpenAsync(options.LineSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            service.Emit(SessionEventType.Failed, SessionEventReasons.OpenFailed, ex.Message);
            link.Dispose();
            Volatile.Write(ref service._terminal, TerminalFailed);
            Volatile.Write(ref service._state, (int)SessionState.Failed);
            service.ShutdownPrimitives();
            // The service is returned even on failure (completed channel, State=Failed)
            // so callers can surface the structured failed event; check Succeeded first.
            return new SessionStartResult(Succeeded: false, service, ex.Message);
        }

        service._link = link;
        service._linkUsable = true;
        Volatile.Write(ref service._state, (int)SessionState.Connected);
        service.Emit(SessionEventType.Connected, SessionEventReasons.OpenSucceeded, null);
        service._sessionRowId = store?.CreateSession(options.PortPath, service._utcNow(), options.ProfileId).Id ?? 0;
        service._monitor = Task.Run(service.MonitorLoopAsync, CancellationToken.None);
        return new SessionStartResult(Succeeded: true, service, Error: null);
    }

    /// <summary>
    /// Sends <paramref name="text"/> as UTF-8 bytes followed by the session line ending.
    /// The exact framed payload lands in the log and the text in send history.
    /// </summary>
    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var payload = new List<byte>(Encoding.UTF8.GetBytes(text));
        payload.AddRange(_options.LineEnding.ToBytes());
        return SendCoreAsync(payload.ToArray(), historyText: text, cancellationToken);
    }

    /// <summary>
    /// Sends raw bytes verbatim with no framing and no history entry — the escape hatch
    /// for binary protocols.
    /// </summary>
    public Task SendRawAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendCoreAsync(payload, historyText: null, cancellationToken);
    }

    /// <summary>
    /// Sends an expanded <see cref="SendPreset"/> verbatim (presets carry their own
    /// explicit escapes) and records the preset text in history.
    /// </summary>
    public Task SendPresetAsync(SendPreset preset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return SendCoreAsync(preset.Expand(), preset.Text, cancellationToken);
    }

    /// <summary>
    /// Signals the monitor loop to stop, unblocks any pending read, and waits for it to
    /// finish. Emits <see cref="SessionEventType.Disconnected"/> with reason
    /// <see cref="SessionEventReasons.UserRequested"/> when the session had not already
    /// failed, then closes the metadata row.
    /// </summary>
    public async Task StopAsync()
    {
        if (Volatile.Read(ref _primitivesDisposed) == 0)
        {
            _shutdown.Cancel();
        }

        // Fail fast for in-flight and late sends; the monitor notices shutdown via ct.
        _linkUsable = false;
        var monitor = _monitor;
        if (monitor is not null)
        {
            try
            {
                await monitor.ConfigureAwait(false);
            }
            finally
            {
                CloseCurrentLink();
            }
        }
        else
        {
            CloseCurrentLink();
        }

        if (Interlocked.CompareExchange(ref _terminal, TerminalStopped, TerminalNone) == TerminalNone)
        {
            Volatile.Write(ref _state, (int)SessionState.Stopped);
        }

        EndMetadataRow();
    }

    /// <summary>Stops the session (if running) and releases all primitives.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        ShutdownPrimitives();
    }

    private async Task SendCoreAsync(byte[] payload, string? historyText, CancellationToken cancellationToken)
    {
        if (!_linkUsable)
        {
            throw new InvalidOperationException(
                $"Session link for '{_options.PortPath}' is not connected (reconnecting or stopped).");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var link = Volatile.Read(ref _link);
            if (!_linkUsable || link is null)
            {
                throw new InvalidOperationException(
                    $"Session link for '{_options.PortPath}' is not connected (reconnecting or stopped).");
            }

            try
            {
                await link.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is LinkDisconnectedException or IOException) && !cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _pendingDropReason, ex is LinkDisconnectedException
                    ? SessionEventReasons.DeviceGone
                    : SessionEventReasons.IoError);
                Interlocked.Exchange(ref _pendingDropDetail, ex.Message);
                _linkUsable = false;
                Volatile.Read(ref _linkAbort).Cancel();
                throw;
            }

            Log.Append(new LogEvent(_utcNow(), LogEventDirection.Sent, payload));
            if (historyText is not null)
            {
                History.Record(_options.PortPath, historyText);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task MonitorLoopAsync()
    {
        var ct = _shutdown.Token;
        try
        {
            while (true)
            {
                var link = Volatile.Read(ref _link)!;
                byte[] data = Array.Empty<byte>();
                string? dropReason = null;
                string? dropDetail = null;
                try
                {
                    using var readGate = CancellationTokenSource.CreateLinkedTokenSource(
                        ct, Volatile.Read(ref _linkAbort).Token);
                    data = await link.ReadAsync(_options.ReadTimeout, readGate.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is LinkDisconnectedException or IOException or OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    if (ex is OperationCanceledException)
                    {
                        dropReason = Interlocked.Exchange(ref _pendingDropReason, null) ?? SessionEventReasons.IoError;
                        dropDetail = Interlocked.Exchange(ref _pendingDropDetail, null);
                    }
                    else if (ex is LinkDisconnectedException)
                    {
                        dropReason = SessionEventReasons.DeviceGone;
                        dropDetail = ex.Message;
                    }
                    else
                    {
                        dropReason = SessionEventReasons.IoError;
                        dropDetail = ex.Message;
                    }
                }

                if (data.Length > 0)
                {
                    Log.Append(new LogEvent(_utcNow(), LogEventDirection.Received, data));
                }

                if (dropReason is null && !ct.IsCancellationRequested)
                {
                    // A failed send marks the link dead without cancelling a blocked
                    // read on every platform loop; notice it even on a successful poll.
                    var pending = Interlocked.Exchange(ref _pendingDropReason, null);
                    if (pending is not null || !_linkUsable)
                    {
                        dropReason = pending ?? SessionEventReasons.IoError;
                        dropDetail = Interlocked.Exchange(ref _pendingDropDetail, null);
                    }
                }

                if (dropReason is null)
                {
                    // Yield when the link reports an idle tick so a fast-returning link
                    // cannot hot-spin the loop; real links block in the OS up to the
                    // read timeout instead of returning empty.
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                _linkUsable = false;
                CloseLink(link);
                var outcome = await ReconnectAsync(dropReason, dropDetail, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case ReconnectOutcome.Resumed:
                        continue;
                    case ReconnectOutcome.Stopped:
                        // Shutdown requested mid-retry: emit the clean user-stop
                        // transition below without clobbering a terminal failure.
                        break;
                    default:
                        if (Interlocked.CompareExchange(ref _terminal, TerminalFailed, TerminalNone) == TerminalNone)
                        {
                            Volatile.Write(ref _state, (int)SessionState.Failed);
                        }

                        return;
                }

                break;
            }

            Emit(SessionEventType.Disconnected, SessionEventReasons.UserRequested, null);
            if (Interlocked.CompareExchange(ref _terminal, TerminalStopped, TerminalNone) == TerminalNone)
            {
                Volatile.Write(ref _state, (int)SessionState.Stopped);
            }
        }
#pragma warning disable CA1031 // Last-resort guard: an unmapped exception must still
        // surface as a structured terminal event instead of an unobserved task fault.
        catch (Exception ex)
        {
            Emit(SessionEventType.Failed, SessionEventReasons.IoError, ex.Message);
            if (Interlocked.CompareExchange(ref _terminal, TerminalFailed, TerminalNone) == TerminalNone)
            {
                Volatile.Write(ref _state, (int)SessionState.Failed);
            }
        }
#pragma warning restore CA1031
    }

    private async Task<ReconnectOutcome> ReconnectAsync(string dropReason, string? dropDetail, CancellationToken ct)
    {
        if (!_options.AutoReconnect)
        {
            Emit(SessionEventType.Failed, SessionEventReasons.ReconnectDisabled, Combine(dropReason, dropDetail));
            return ReconnectOutcome.Failed;
        }

        Volatile.Write(ref _state, (int)SessionState.Reconnecting);
        var reason = dropReason;
        var detail = dropDetail;
        for (var attempt = 1; attempt <= _options.MaxReconnectAttempts; attempt++)
        {
            Emit(SessionEventType.ReconnectAttempt, reason, detail, attempt);
            try
            {
                await Task.Delay(_options.ReconnectBackoff(attempt - 1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ReconnectOutcome.Stopped;
            }

            var candidate = _factory.Create(_options.PortPath);
            try
            {
                await candidate.OpenAsync(_options.LineSettings, ct).ConfigureAwait(false);
                Volatile.Write(ref _link, candidate);
                _linkUsable = true;
                var previousAbort = Interlocked.Exchange(ref _linkAbort, new CancellationTokenSource());
                previousAbort.Dispose();
                Volatile.Write(ref _state, (int)SessionState.Connected);
                Emit(SessionEventType.Connected, SessionEventReasons.ReconnectSucceeded, null, attempt);
                return ReconnectOutcome.Resumed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                candidate.Dispose();
                if (ct.IsCancellationRequested)
                {
                    return ReconnectOutcome.Stopped;
                }

                reason = SessionEventReasons.OpenFailed;
                detail = ex.Message;
            }
        }

        Emit(SessionEventType.Failed, SessionEventReasons.ReconnectAbandoned, detail);
        return ReconnectOutcome.Failed;
    }

    private enum ReconnectOutcome
    {
        Resumed,
        Stopped,
        Failed,
    }

    private void Emit(SessionEventType type, string reason, string? detail, int attempt = 0) =>
        _ = _events.Writer.TryWrite(new SessionEvent(_utcNow(), type, _options.PortPath, reason, detail, attempt));

    private static string Combine(string reason, string? detail) =>
        detail is null ? reason : $"{reason}: {detail}";

    private void CloseCurrentLink()
    {
        var link = Interlocked.Exchange(ref _link, null);
        CloseLink(link);
    }

    private static void CloseLink(ISerialLink? link)
    {
        if (link is null)
        {
            return;
        }

        try
        {
            link.CloseAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Closing an already-dead link is expected during drops.
        }

        link.Dispose();
    }

    private void EndMetadataRow()
    {
        var rowId = Interlocked.Exchange(ref _sessionRowId, 0);
        if (rowId != 0)
        {
            _ = _store?.EndSession(rowId, _utcNow());
        }
    }

    private void ShutdownPrimitives()
    {
        if (Interlocked.Exchange(ref _primitivesDisposed, 1) != 0)
        {
            return;
        }

        _events.Writer.TryComplete();
        _writeLock.Dispose();
        _shutdown.Dispose();
        _linkAbort.Dispose();
    }
}

/// <summary>
/// Outcome of <see cref="SessionService.StartAsync"/> /
/// <see cref="SessionService.TryStartAsync"/>. On failure the service is still
/// returned with <see cref="SessionService.State"/> set to
/// <see cref="SessionState.Failed"/> and its event channel completed, so callers can
/// surface the structured <c>failed</c>/<c>open-failed</c> event; check
/// <see cref="Succeeded"/> before interacting with it.
/// </summary>
/// <param name="Succeeded">Whether the session opened successfully.</param>
/// <param name="Service">The session service (running on success, failed otherwise).</param>
/// <param name="Error">Failure detail mirroring the emitted event, or <see langword="null"/> on success.</param>
public sealed record SessionStartResult(
    bool Succeeded,
    SessionService Service,
    string? Error);
