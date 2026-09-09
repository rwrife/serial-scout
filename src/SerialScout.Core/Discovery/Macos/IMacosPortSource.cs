namespace SerialScout.Core.Discovery.Macos;

/// <summary>
/// Seam over macOS port enumeration so <see cref="MacosSerialDiscovery"/> can be exercised
/// with fixtures on any host. The production implementation shells out to <c>ioreg</c> and
/// probes availability through libc.
/// </summary>
public interface IMacosPortSource
{
    /// <summary>True when the current OS can enumerate serial ports at all.</summary>
    bool IsSupportedPlatform { get; }

    /// <summary>
    /// Reads serial clients with USB identity metadata gathered from their IOKit ancestry.
    /// Implementations perform the advisory-lock availability probe for each port.
    /// </summary>
    IReadOnlyList<(MacosSerialEntry Entry, MacosProbeResult Probe)> ReadEntries();
}
