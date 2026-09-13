using SerialScout.Core.Discovery;

namespace SerialScout.App.Tests;

/// <summary>
/// In-memory <see cref="ISerialDiscovery"/> for view-model tests: the test scripts the
/// port list, optionally arming a failure to exercise error paths.
/// </summary>
public sealed class FakeSerialDiscovery : ISerialDiscovery
{
    private readonly List<NormalizedPort> _ports = [];

    /// <summary>When set, <see cref="Discover"/> throws it once.</summary>
    public Exception? FailNext { get; set; }

    /// <inheritdoc />
    public string PlatformId => "fake";

    /// <summary>Adds a scripted port for the next and all later scans.</summary>
    public FakeSerialDiscovery Add(NormalizedPort port)
    {
        _ports.Add(port);
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyList<NormalizedPort> Discover()
    {
        if (FailNext is not null)
        {
            var fault = FailNext;
            FailNext = null;
            throw fault;
        }

        return _ports.ToArray();
    }
}
