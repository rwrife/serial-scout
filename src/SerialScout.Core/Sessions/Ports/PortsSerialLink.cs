using System.IO.Ports;
using SerialScout.Core.Profiles;

namespace SerialScout.Core.Sessions.Ports;

/// <summary>
/// Production <see cref="ISerialLink"/> backed by <see cref="SerialPort"/>.
///
/// Semantics chosen to match the session-engine contract:
/// <list type="bullet">
/// <item>Reads are synchronous inside a worker task with <see cref="SerialPort.ReadTimeout"/>
/// set from the engine's poll timeout, so a blocked read always returns within the timeout.
/// This keeps stop/reconnect latency bounded on every OS, at the cost of a thread-pool hop
/// per poll tick. The cancellation token is honored between ticks and before starting an
/// OS read, not mid-OS-read (tracked for a native rework in issue #12).</item>
/// <item><see cref="DtrEnable"/> and <see cref="RtsEnable"/> stay <see langword="false"/>:
/// asserting them on open can reset attached boards, so they are opt-in by callers later.</item>
/// <item>A vanished device surfaces as <see cref="LinkDisconnectedException"/> so the
/// engine's reconnect path engages; plain OS IO errors propagate as
/// <see cref="IOException"/>.</item>
/// </list>
/// <remarks>
/// <para>
/// <c>System.IO.Ports</c> is fully supported by Microsoft on Windows only; the Unix/macOS
/// implementation in the runtime is explicitly "not recommended for production use". It is
/// good enough for preview builds on those platforms (it opens and exchanges data with the
/// common USB-serial drivers), and issue #12 tracks replacing it with a native termios
/// backend for macOS.
/// </para>
/// </remarks>
public sealed class PortsSerialLink : ISerialLink
{
    /// <summary>Maximum byte chunk returned by one <see cref="ReadAsync"/> call.</summary>
    public const int ReadBufferSize = 4096;

    private const int DefaultWriteTimeoutMs = 5000;

    private readonly SerialPort _port = new();
    private bool _open;
    private bool _disposed;

    /// <summary>Creates a link for the given OS port path without opening it.</summary>
    /// <param name="portPath">OS port path, e.g. <c>COM7</c> or <c>/dev/cu.usbserial-110</c>.</param>
    public PortsSerialLink(string portPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portPath);
        PortPath = portPath.Trim();
        _port.PortName = PortPath;
        _port.DtrEnable = false;
        _port.RtsEnable = false;
        _port.Handshake = Handshake.None;
    }

    /// <inheritdoc />
    public string PortPath { get; }

    /// <inheritdoc />
    public LineSettings LineSettings { get; private set; } = LineSettings.Default;

    /// <inheritdoc />
    public async Task OpenAsync(LineSettings lineSettings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineSettings);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        _port.BaudRate = lineSettings.BaudRate;
        _port.DataBits = lineSettings.DataBits;
        _port.Parity = lineSettings.Parity switch
        {
            LineParity.None => Parity.None,
            LineParity.Odd => Parity.Odd,
            LineParity.Even => Parity.Even,
            _ => throw new ArgumentOutOfRangeException(nameof(lineSettings), lineSettings.Parity, "Unknown parity."),
        };
        _port.StopBits = lineSettings.StopBits switch
        {
            LineStopBits.One => StopBits.One,
            LineStopBits.Two => StopBits.Two,
            _ => throw new ArgumentOutOfRangeException(nameof(lineSettings), lineSettings.StopBits, "Unknown stop bits."),
        };
        _port.WriteTimeout = DefaultWriteTimeoutMs;

        try
        {
            await Task.Run(() => _port.Open(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or NotSupportedException or TimeoutException)
        {
            // Normalize the less-common open failures onto the IOException/
            // UnauthorizedAccessException/InvalidOperationException surface that
            // SessionService.TryStartAsync catches, so failed opens always produce a
            // structured open-failed event instead of escaping as an unclassified throw.
            throw new IOException($"Failed to open '{PortPath}': {ex.Message}", ex);
        }

        _open = true;
        LineSettings = lineSettings;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        var payload = data.ToArray();
        try
        {
            await Task.Run(() => _port.Write(payload, 0, payload.Length), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw MapIo(ex, "write");
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        var timeoutMs = Math.Clamp((int)Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);
        _port.ReadTimeout = timeoutMs;
        var buffer = new byte[ReadBufferSize];
        try
        {
            var read = await Task.Run(() => ReadOnce(buffer, timeoutMs), CancellationToken.None)
                .ConfigureAwait(false);
            return read == 0 ? Array.Empty<byte>() : buffer[..read];
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw MapIo(ex, "read");
        }
    }

    /// <inheritdoc />
    public Task CloseAsync()
    {
        if (!_open)
        {
            return Task.CompletedTask;
        }

        _open = false;
        try
        {
            _port.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Tolerate double-close and already-dead handles per the interface contract.
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = CloseAsync();
        _port.Dispose();
    }

    private int ReadOnce(byte[] buffer, int timeoutMs)
    {
        try
        {
            return _port.Read(buffer, 0, buffer.Length);
        }
        catch (TimeoutException)
        {
            // Idle poll tick: the engine treats an empty read as "nothing yet".
            _ = timeoutMs;
            return 0;
        }
    }

    private IOException MapIo(Exception ex, string operation)
    {
        if (_disposed || !_open || !_port.IsOpen)
        {
            return new LinkDisconnectedException(PortPath, $"{operation}: {ex.Message}");
        }

        if (ex is UnauthorizedAccessException)
        {
            return new IOException($"{operation} on '{PortPath}' denied: {ex.Message}", ex);
        }

        return new IOException($"{operation} on '{PortPath}' failed: {ex.Message}", ex);
    }

    private void EnsureOpen()
    {
        if (!_open || !_port.IsOpen)
        {
            throw new InvalidOperationException($"Link for '{PortPath}' is not open.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
