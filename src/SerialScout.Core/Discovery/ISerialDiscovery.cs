namespace SerialScout.Core.Discovery;

/// <summary>
/// Abstraction for a platform-specific serial-port discovery adapter.
/// Adapters collect raw operating-system metadata and normalize it through
/// <see cref="PortNormalizer"/> so downstream profile matching never depends on
/// unstable COM/tty naming or OS-specific metadata shapes.
/// </summary>
public interface ISerialDiscovery
{
    /// <summary>Stable identifier for the platform backing this adapter (for example "windows" or "macos").</summary>
    string PlatformId { get; }

    /// <summary>
    /// Enumerates serial ports once and returns them normalized and ordered with
    /// <see cref="PortPathComparer"/>. Implementations must not fully open ports during
    /// discovery: opening a serial device can toggle DTR/RTS and reset attached boards.
    /// </summary>
    IReadOnlyList<NormalizedPort> Discover();
}
