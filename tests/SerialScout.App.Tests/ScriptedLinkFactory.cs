using System.Threading.Channels;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;

namespace SerialScout.App.Tests;

/// <summary>
/// Minimal in-memory <see cref="ISerialLink"/> + factory for terminal view-model tests,
/// mirroring the engine test doubles: pushes come from the test, faults can be armed.
/// </summary>
public sealed class ScriptedLinkFactory : ISerialLinkFactory
{
    private readonly Func<string, ISerialLink> _selector;

    /// <summary>Creates the factory with a per-path link selector.</summary>
    public ScriptedLinkFactory(Func<string, ISerialLink>? selector = null)
        => _selector = selector ?? (static path => new ScriptedLink(path));

    /// <summary>Links handed out so far.</summary>
    public List<ScriptedLink> Created { get; } = [];

    /// <inheritdoc />
    public ISerialLink Create(string portPath)
    {
        var link = (ScriptedLink)_selector(portPath);
        Created.Add(link);
        return link;
    }
}

/// <inheritdoc cref="ScriptedLinkFactory" />
public sealed class ScriptedLink : ISerialLink
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();

    /// <summary>Creates a link for a fake path.</summary>
    public ScriptedLink(string portPath) => PortPath = portPath;

    /// <inheritdoc />
    public string PortPath { get; }

    /// <inheritdoc />
    public LineSettings LineSettings { get; private set; } = LineSettings.Default;

    /// <summary>When set, the next open throws it once.</summary>
    public Exception? OpenFault { get; set; }

    /// <summary>All write payloads in order.</summary>
    public List<byte[]> Writes { get; } = [];

    /// <summary>Pushes bytes the engine should observe as received traffic.</summary>
    public void Push(byte[] data) => _incoming.Writer.TryWrite(data);

    /// <inheritdoc />
    public Task OpenAsync(LineSettings lineSettings, CancellationToken cancellationToken)
    {
        if (OpenFault is not null)
        {
            var fault = OpenFault;
            OpenFault = null;
            return Task.FromException(fault);
        }

        LineSettings = lineSettings;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        Writes.Add(data.ToArray());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_incoming.Reader.TryRead(out var chunk))
            {
                return chunk;
            }

            if (_incoming.Reader.Completion.IsCompleted)
            {
                throw new OperationCanceledException();
            }

            await Task.Delay(2, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task CloseAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _incoming.Writer.TryComplete();
}
