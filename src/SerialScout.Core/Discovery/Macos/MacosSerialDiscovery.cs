namespace SerialScout.Core.Discovery.Macos;

/// <summary>
/// macOS discovery adapter: enumerates IOKit serial clients with USB identity metadata,
/// probes availability, and normalizes everything into <see cref="NormalizedPort"/> values.
/// On non-macOS hosts it returns an empty list rather than throwing.
/// </summary>
public sealed class MacosSerialDiscovery : ISerialDiscovery
{
    private readonly IMacosPortSource _source;

    /// <summary>Creates the adapter with the production ioreg source.</summary>
    public MacosSerialDiscovery()
        : this(new IoregMacosPortSource())
    {
    }

    /// <summary>Creates the adapter over a custom port source (fixtures/tests).</summary>
    public MacosSerialDiscovery(IMacosPortSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public string PlatformId => "macos";

    /// <inheritdoc />
    public IReadOnlyList<NormalizedPort> Discover()
    {
        if (!_source.IsSupportedPlatform)
        {
            return Array.Empty<NormalizedPort>();
        }

        var raws = _source
            .ReadEntries()
            .Select(pair => MacosPortMapper.ToRawRecord(pair.Entry, pair.Probe))
            .Where(record => record is not null)
            .Select(record => record!);

        return PortNormalizer.NormalizeAll(raws);
    }
}
