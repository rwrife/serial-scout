namespace SerialScout.Core.Discovery.Windows;

/// <summary>
/// Windows discovery adapter: reads the PnP device store, parses COM naming and USB
/// identity metadata, and normalizes everything into <see cref="NormalizedPort"/> values.
/// On non-Windows hosts it returns an empty list rather than throwing.
/// </summary>
public sealed class WindowsSerialDiscovery : ISerialDiscovery
{
    private readonly IWindowsPnpRowSource _source;

    /// <summary>Creates the adapter with the production SetupAPI source.</summary>
    public WindowsSerialDiscovery()
        : this(new WindowsSetupApiSource())
    {
    }

    /// <summary>Creates the adapter over a custom row source (fixtures/tests).</summary>
    public WindowsSerialDiscovery(IWindowsPnpRowSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public string PlatformId => "windows";

    /// <inheritdoc />
    public IReadOnlyList<NormalizedPort> Discover()
    {
        if (!_source.IsSupportedPlatform)
        {
            return Array.Empty<NormalizedPort>();
        }

        var raws = _source
            .ReadRows()
            .Select(WindowsPnpParser.Parse)
            .Where(record => record is not null)
            .Select(record => record!);

        return PortNormalizer.NormalizeAll(raws);
    }
}
