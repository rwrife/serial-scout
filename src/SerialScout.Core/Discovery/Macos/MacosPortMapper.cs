namespace SerialScout.Core.Discovery.Macos;

/// <summary>
/// Result of the non-invasive macOS port probe. The production probe opens the callout
/// device with <c>O_EXLOCK | O_NONBLOCK</c> and no access mode: it acquires the advisory
/// lock BSD terminals use, without reading, writing, or changing terminal settings.
/// </summary>
public enum MacosProbeResult
{
    /// <summary>No probe attempted; state stays <see cref="ScanState.Unknown"/>.</summary>
    NotProbed,

    /// <summary>The advisory lock was acquired, so no other session holds the port.</summary>
    Available,

    /// <summary>The advisory lock is held elsewhere (cu, screen, Serial Scout session).</summary>
    Locked,

    /// <summary>The OS refused access to the device node.</summary>
    PermissionDenied,

    /// <summary>The device node disappeared between enumeration and probe.</summary>
    PortGone,

    /// <summary>An unexpected errno; health cannot be determined.</summary>
    OtherError,
}

/// <summary>
/// Pure mapping from macOS parser output + probe result to <see cref="RawPortRecord"/>.
/// </summary>
public static class MacosPortMapper
{
    /// <summary>Maps a raw errno from the advisory-lock probe to a probe result.</summary>
    public static MacosProbeResult MapProbeError(int errno) => errno switch
    {
        0 => MacosProbeResult.Available,
        ErrorWouldBlock => MacosProbeResult.Locked,
        ErrorPermissionDenied or ErrorOperationNotPermitted => MacosProbeResult.PermissionDenied,
        ErrorNoSuchFile => MacosProbeResult.PortGone,
        _ => MacosProbeResult.OtherError,
    };

    /// <summary>
    /// Converts one parsed ioreg entry and probe outcome into a raw record.
    /// Returns <c>null</c> for vanished ports or entries without a device path.
    /// </summary>
    public static RawPortRecord? ToRawRecord(MacosSerialEntry entry, MacosProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (probe == MacosProbeResult.PortGone || string.IsNullOrWhiteSpace(entry.PortPath))
        {
            return null;
        }

        return new RawPortRecord(
            PortPath: entry.PortPath.Trim(),
            DisplayName: null,
            VendorId: entry.VendorIdHex,
            ProductId: entry.ProductIdHex,
            Manufacturer: entry.Manufacturer,
            Product: entry.Product,
            SerialNumber: entry.SerialNumber,
            Availability: MapProbe(probe),
            Notes: probe == MacosProbeResult.OtherError
                ? "availability-probe-failed"
                : null);
    }

    private static RawPortAvailability MapProbe(MacosProbeResult probe) => probe switch
    {
        MacosProbeResult.Available => RawPortAvailability.Available,
        MacosProbeResult.Locked => RawPortAvailability.InUse,
        MacosProbeResult.PermissionDenied => RawPortAvailability.AccessDenied,
        _ => RawPortAvailability.Unknown,
    };

    // Darwin errno values used by the probe mapping.
    private const int ErrorOperationNotPermitted = 1;
    private const int ErrorNoSuchFile = 2;
    private const int ErrorPermissionDenied = 13;
    private const int ErrorWouldBlock = 35;
}
