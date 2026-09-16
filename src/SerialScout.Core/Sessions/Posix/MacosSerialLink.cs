using SerialScout.Core.Profiles;

namespace SerialScout.Core.Sessions.Posix;

/// <summary>
/// macOS serial link backed directly by Darwin termios and poll. It never uses
/// <c>System.IO.Ports</c>.
/// </summary>
public sealed class MacosSerialLink : ISerialLink
{
    /// <summary>Maximum byte chunk returned by one read.</summary>
    public const int ReadBufferSize = 4096;

    internal const int MaximumPollSliceMilliseconds = 25;

    private readonly object _sync = new();
    private readonly IDarwinNativeApi _api;
    private readonly Func<long> _tickCount = static () => Environment.TickCount64;
    private readonly bool _enforceMacOS;
    private readonly HashSet<OperationContext> _operations = [];

    private int _descriptor = -1;
    private bool _opening;
    private bool _closing;
    private bool _disposed;
    private bool _disconnectReported;

    /// <summary>Creates a link for a macOS device path without opening it.</summary>
    public MacosSerialLink(string portPath)
        : this(portPath, new DarwinNativeApi(), enforceMacOS: true)
    {
    }

    internal MacosSerialLink(string portPath, IDarwinNativeApi api, Func<long>? tickCount = null)
        : this(portPath, api, enforceMacOS: false)
    {
        _tickCount = tickCount ?? _tickCount;
    }

    private MacosSerialLink(string portPath, IDarwinNativeApi api, bool enforceMacOS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portPath);
        if (portPath.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Serial port path cannot contain an embedded NUL.", nameof(portPath));
        }

        ArgumentNullException.ThrowIfNull(api);
        PortPath = portPath.Trim();
        _api = api;
        _enforceMacOS = enforceMacOS;
    }

    /// <inheritdoc />
    public string PortPath { get; }

    /// <inheritdoc />
    public LineSettings LineSettings { get; private set; } = LineSettings.Default;

    /// <inheritdoc />
    public Task OpenAsync(LineSettings lineSettings, CancellationToken cancellationToken)
    {
        DarwinTerminalSettings.Validate(lineSettings);
        cancellationToken.ThrowIfCancellationRequested();

        if (_enforceMacOS && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The native termios link is available only on macOS.");
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_descriptor >= 0 || _opening)
            {
                throw new InvalidOperationException($"Link for '{PortPath}' is already open.");
            }

            _opening = true;
        }

        var descriptor = -1;
        try
        {
            descriptor = _api.Open(PortPath, DarwinConstants.SerialOpenFlags);
            if (descriptor < 0)
            {
                throw NativeFailure("open");
            }

            if (_api.SetExclusive(descriptor) != 0)
            {
                throw NativeFailure("TIOCEXCL");
            }

            DarwinTerminalSettings.Configure(_api, descriptor, lineSettings);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_closing)
                {
                    throw new OperationCanceledException("Serial open was aborted.");
                }

                _descriptor = descriptor;
                descriptor = -1;
                _disconnectReported = false;
                LineSettings = lineSettings;
            }

            return Task.CompletedTask;
        }
        finally
        {
            if (descriptor >= 0)
            {
                _ = _api.Close(descriptor);
            }

            lock (_sync)
            {
                _opening = false;
                Monitor.PulseAll(_sync);
            }
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = BeginOperation();

        if (data.IsEmpty)
        {
            return;
        }

        var payload = data.ToArray();
        await Task.Run(
            () => WriteCore(operation, payload, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout cannot be negative.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var operation = BeginOperation();
        return await Task.Run(
            () => ReadCore(operation, timeout, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task CloseAsync() => Task.Run(CloseCore);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CloseCore();
    }

    private OperationContext BeginOperation()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_descriptor < 0 || _closing)
            {
                throw new InvalidOperationException($"Link for '{PortPath}' is not open.");
            }

            var operation = new OperationContext(this, _descriptor);
            _operations.Add(operation);
            return operation;
        }
    }

    private byte[] ReadCore(
        OperationContext operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timeoutMilliseconds = Math.Clamp(
            (long)Math.Ceiling(timeout.TotalMilliseconds),
            0,
            int.MaxValue);
        var deadline = _tickCount() + timeoutMilliseconds;
        var buffer = new byte[ReadBufferSize];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = RemainingMilliseconds(deadline);
            var returnedEvents = Poll(
                operation,
                DarwinConstants.Readable,
                remaining,
                cancellationToken);
            if (returnedEvents == 0)
            {
                return [];
            }

            var pollReportedHangup =
                (returnedEvents & DarwinConstants.PollHangUp) != 0;
            var count = _api.Read(operation.SerialDescriptor, buffer, buffer.Length);
            if (count > 0)
            {
                return buffer[..checked((int)count)];
            }

            if (count == 0)
            {
                if (pollReportedHangup)
                {
                    throw Disconnected("read drained buffered data after hangup");
                }

                if (RemainingMilliseconds(deadline) == 0)
                {
                    return [];
                }

                continue;
            }

            var error = _api.GetLastError();
            if (error == DarwinConstants.Interrupted)
            {
                continue;
            }

            if (error == DarwinConstants.TryAgain)
            {
                if (pollReportedHangup)
                {
                    throw Disconnected("read drained buffered data after hangup");
                }

                if (RemainingMilliseconds(deadline) == 0)
                {
                    return [];
                }

                continue;
            }

            throw MapIoFailure("read", error);
        }
    }

    private void WriteCore(
        OperationContext operation,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < payload.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Poll(operation, DarwinConstants.Writable, Timeout.Infinite, cancellationToken);

            var count = _api.Write(
                operation.SerialDescriptor,
                payload,
                offset,
                payload.Length - offset);
            if (count > 0)
            {
                offset += checked((int)count);
                continue;
            }

            if (count == 0)
            {
                throw Disconnected("write made no progress");
            }

            var error = _api.GetLastError();
            if (error is DarwinConstants.Interrupted or DarwinConstants.TryAgain)
            {
                continue;
            }

            throw MapIoFailure("write", error);
        }
    }

    private int Poll(
        OperationContext operation,
        int requestedEvents,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var deadline = timeoutMilliseconds == Timeout.Infinite
            ? long.MaxValue
            : _tickCount() + timeoutMilliseconds;
        var firstPoll = true;

        while (true)
        {
            ThrowIfAborted(operation, cancellationToken);
            var remaining = deadline == long.MaxValue
                ? MaximumPollSliceMilliseconds
                : RemainingMilliseconds(deadline);
            if (!firstPoll && remaining == 0)
            {
                return 0;
            }

            firstPoll = false;
            var descriptors = new[]
            {
                new DarwinPollDescriptor
                {
                    Descriptor = operation.SerialDescriptor,
                    Events = checked((short)requestedEvents),
                },
            };
            var slice = Math.Min(remaining, MaximumPollSliceMilliseconds);
            var result = _api.Poll(descriptors, slice);
            ThrowIfAborted(operation, cancellationToken);

            if (result == 0)
            {
                if (deadline != long.MaxValue && RemainingMilliseconds(deadline) == 0)
                {
                    return 0;
                }

                continue;
            }

            if (result < 0)
            {
                var error = _api.GetLastError();
                if (error == DarwinConstants.Interrupted)
                {
                    if (deadline != long.MaxValue && RemainingMilliseconds(deadline) == 0)
                    {
                        return 0;
                    }

                    continue;
                }

                throw MapIoFailure("poll", error);
            }

            var returnedEvents = descriptors[0].ReturnedEvents;
            if ((returnedEvents &
                (DarwinConstants.PollError | DarwinConstants.PollInvalid)) != 0 ||
                (returnedEvents & DarwinConstants.PollHangUp) != 0 &&
                (requestedEvents != DarwinConstants.Readable ||
                 (returnedEvents & DarwinConstants.Readable) == 0))
            {
                throw Disconnected("poll reported that the device disappeared");
            }

            if ((returnedEvents & requestedEvents) != 0)
            {
                return returnedEvents;
            }

            if (deadline != long.MaxValue && RemainingMilliseconds(deadline) == 0)
            {
                return 0;
            }
        }
    }

    private static void ThrowIfAborted(
        OperationContext operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation.IsAborted)
        {
            throw new OperationCanceledException("Serial operation was aborted by close.");
        }
    }

    private void CloseCore()
    {
        int descriptor;
        lock (_sync)
        {
            while (_closing)
            {
                Monitor.Wait(_sync);
            }

            if (_descriptor < 0 && !_opening)
            {
                return;
            }

            _closing = true;
            foreach (var operation in _operations)
            {
                operation.Abort();
            }

            while (_opening || _operations.Count != 0)
            {
                Monitor.Wait(_sync);
            }

            descriptor = _descriptor;
            if (descriptor >= 0)
            {
                _ = _api.Close(descriptor);
            }

            _descriptor = -1;
            _closing = false;
            Monitor.PulseAll(_sync);
        }
    }

    private IOException MapIoFailure(string operation, int error)
        => IsDisconnectError(error)
            ? Disconnected($"{operation} failed with errno {error}")
            : new IOException($"{operation} on '{PortPath}' failed with errno {error}.");

    private IOException Disconnected(string detail)
    {
        lock (_sync)
        {
            if (!_disconnectReported)
            {
                _disconnectReported = true;
                return new LinkDisconnectedException(PortPath, detail);
            }
        }

        return new IOException($"Serial device '{PortPath}' is no longer available: {detail}.");
    }

    private IOException NativeFailure(string operation)
        => new($"{operation} on '{PortPath}' failed with errno {_api.GetLastError()}.");

    private static bool IsDisconnectError(int error)
        => error is
            DarwinConstants.IoError or
            DarwinConstants.NoDeviceOrAddress or
            DarwinConstants.BadDescriptor or
            DarwinConstants.NoDevice;

    private int RemainingMilliseconds(long deadline)
        => checked((int)Math.Clamp(deadline - _tickCount(), 0, int.MaxValue));

    private sealed class OperationContext : IDisposable
    {
        private readonly MacosSerialLink _owner;
        private int _aborted;
        private bool _disposed;

        internal OperationContext(MacosSerialLink owner, int serialDescriptor)
        {
            _owner = owner;
            SerialDescriptor = serialDescriptor;
        }

        internal int SerialDescriptor { get; }

        internal bool IsAborted => Volatile.Read(ref _aborted) != 0;

        internal void Abort() => Volatile.Write(ref _aborted, 1);

        public void Dispose()
        {
            lock (_owner._sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _owner._operations.Remove(this);
                Monitor.PulseAll(_owner._sync);
            }
        }
    }
}
