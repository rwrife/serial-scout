namespace SerialScout.Core.Discovery.Windows;

/// <summary>
/// Seam over the Windows PnP device store so <see cref="WindowsSerialDiscovery"/> can be
/// exercised with fixtures on any host. The production implementation uses SetupAPI.
/// </summary>
public interface IWindowsPnpRowSource
{
    /// <summary>True when the current OS can answer PnP queries at all.</summary>
    bool IsSupportedPlatform { get; }

    /// <summary>
    /// Reads one present-device row per enumerated Ports-class device. Implementations
    /// perform the non-invasive availability probe for each addressable port.
    /// </summary>
    IReadOnlyList<WindowsPnpRow> ReadRows();
}
