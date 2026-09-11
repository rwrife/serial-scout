using System.Threading.Channels;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;

namespace SerialScout.Core.Tests;

/// <summary>
/// In-memory <see cref="ISerialLink"/> used by session-engine tests: incoming bytes are
/// pushed by the test, writes are recorded, and open/read/write/close faults can be
/// armed to simulate drops without hardware.
/// </summary>
public sealed class FakeSerialLink : ISerialLink
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly List<byte[]> _writes = [];

    public FakeSerialLink(string portPath = "/dev/fake0")
    {
        PortPath = portPath;
    }

    public string PortPath { get; }

    public LineSettings LineSettings { get; private set; } = LineSettings.Default;

    public int OpenCount { get; private set; }

    public int CloseCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>Settings captured from the most recent successful open.</summary>
    public LineSettings? OpenedSettings { get; private set; }

    /// <summary>All write payloads in order.</summary>
    public IReadOnlyList<byte[]> Writes
    {
        get
        {
            lock (_writes)
            {
                return _writes.ToArray();
            }
        }
    }

    /// <summary>When set, the next open throws this and (unless <see cref="OpenFaultOnce"/>) every open after.</summary>
    public Exception? OpenFault { get; set; }

    /// <summary>When true, <see cref="OpenFault"/> fires once and is then cleared.</summary>
    public bool OpenFaultOnce { get; set; }

    /// <summary>When set, the next read throws this once and is then cleared.</summary>
    public Exception? ReadFault { get; set; }

    /// <summary>When set, every write throws this.</summary>
    public Exception? WriteFault { get; set; }

    /// <summary>
    /// When true, an idle read blocks until data is pushed, the channel completes, or
    /// cancellation is requested (mimics a blocking OS read that a cancelled CTS must
    /// interrupt).
    /// </summary>
    public bool BlockWhenEmpty { get; set; }

    public void PushIncoming(byte[] data) => _incoming.Writer.TryWrite(data);

    public void CompleteIncoming() => _incoming.Writer.TryComplete();

    public Task OpenAsync(LineSettings lineSettings, CancellationToken cancellationToken)
    {
        if (OpenFault is not null)
        {
            var fault = OpenFault;
            if (OpenFaultOnce)
            {
                OpenFault = null;
            }

            return Task.FromException(fault);
        }

        OpenCount++;
        OpenedSettings = lineSettings;
        LineSettings = lineSettings;
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (WriteFault is not null)
        {
            return Task.FromException(WriteFault);
        }

        lock (_writes)
        {
            _writes.Add(data.ToArray());
        }

        return Task.CompletedTask;
    }

    public async Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_incoming.Reader.TryRead(out var data))
        {
            return data;
        }

        if (ReadFault is not null)
        {
            var fault = ReadFault;
            ReadFault = null;
            return await Task.FromException<byte[]>(fault).ConfigureAwait(false);
        }

        if (BlockWhenEmpty)
        {
            // Block like a real OS read: data, channel completion, or cancellation are
            // the only ways out. Cancellation raises OperationCanceledException exactly
            // as a cancelled native read would; completion simulates the link dying.
            while (true)
            {
                if (_incoming.Reader.TryRead(out var chunk))
                {
                    return chunk;
                }

                if (_incoming.Reader.Completion.IsCompleted)
                {
                    throw new OperationCanceledException("Incoming channel completed.");
                }

                await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }
        }

        return Array.Empty<byte>();
    }

    public Task CloseAsync()
    {
        CloseCount++;
        return Task.CompletedTask;
    }

    public void Dispose() => DisposeCount++;
}
