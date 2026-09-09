using System.Globalization;
using System.Text.RegularExpressions;

namespace SerialScout.Core.Discovery.Windows;

/// <summary>
/// A single row from the Windows PnP device store, as surfaced by SetupAPI. Kept as plain
/// strings so the parsing logic stays pure and fixture-testable without touching the OS.
/// </summary>
/// <param name="InstanceId">Device instance path, e.g. <c>USB\VID_2341&amp;PID_0043\5&amp;abc&amp;0&amp;2</c>.</param>
/// <param name="FriendlyName">PnP friendly name, typically ending in <c>(COM7)</c>.</param>
/// <param name="DeviceDescription">PnP device description fallback.</param>
/// <param name="HardwareIds">Hardware id strings from the device registry.</param>
/// <param name="Probe">Result of the optional availability probe.</param>
public sealed record WindowsPnpRow(
    string InstanceId,
    string? FriendlyName,
    string? DeviceDescription,
    IReadOnlyList<string> HardwareIds,
    WindowsProbeResult Probe = WindowsProbeResult.NotProbed);

/// <summary>
/// Outcome of the non-invasive Windows port probe (a zero-access <c>CreateFile</c>, which
/// does not reconfigure the port or toggle modem lines).
/// </summary>
public enum WindowsProbeResult
{
    /// <summary>No probe was attempted; state stays <see cref="ScanState.Unknown"/>.</summary>
    NotProbed,

    /// <summary>The port could be opened for querying, so nothing else holds it.</summary>
    Available,

    /// <summary>The driver refused a second handle: the port is held by another process.</summary>
    InUse,

    /// <summary>The port no longer exists (vanished between enumeration and probe).</summary>
    PortGone,

    /// <summary>An unexpected Win32 error; health cannot be determined.</summary>
    OtherError,
}

/// <summary>
/// Pure parser turning raw Windows PnP rows into <see cref="RawPortRecord"/> values.
/// </summary>
public static class WindowsPnpParser
{
    private static readonly Regex UsbVidPidPattern = new(
        @"USB\\VID_(?<vid>[0-9a-fA-F]{4})&PID_(?<pid>[0-9a-fA-F]{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ComPortPattern = new(
        @"\(\s*(?<com>COM[0-9]+)\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Maps a raw Win32 error code from the probe open to a probe result.
    /// Error 5 (access denied) and 32 (sharing violation) both surface the same driver
    /// message for "port already open", so both map to <see cref="WindowsProbeResult.InUse"/>.
    /// </summary>
    public static WindowsProbeResult MapProbeError(int win32Error) => win32Error switch
    {
        0 => WindowsProbeResult.Available,
        ErrorFileNotFound or ErrorNoSuchDevice => WindowsProbeResult.PortGone,
        ErrorAccessDenied or ErrorSharingViolation => WindowsProbeResult.InUse,
        _ => WindowsProbeResult.OtherError,
    };

    /// <summary>
    /// Parses one PnP row. Returns <c>null</c> when the row cannot be addressed as a COM
    /// port (no <c>(COMx)</c> suffix anywhere) or when the device disappeared mid-scan.
    /// </summary>
    public static RawPortRecord? Parse(WindowsPnpRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Probe == WindowsProbeResult.PortGone)
        {
            return null;
        }

        var portPath = ExtractComPort(row.FriendlyName) ?? ExtractComPort(row.DeviceDescription);
        if (portPath is null)
        {
            return null;
        }

        var (vendorId, productId) = ExtractUsbIds(row.HardwareIds);
        var serial = ExtractInstanceSerial(row.InstanceId);
        var displayName = FirstNonEmpty(row.FriendlyName, row.DeviceDescription);

        return new RawPortRecord(
            PortPath: portPath,
            DisplayName: displayName,
            VendorId: vendorId,
            ProductId: productId,
            Manufacturer: null,
            Product: StripTrailingPortSuffix(displayName),
            SerialNumber: serial,
            Availability: MapProbe(row.Probe),
            Notes: row.Probe == WindowsProbeResult.OtherError
                ? "availability-probe-failed"
                : null);
    }

    /// <summary>Extracts the COM port name from a trailing <c>(COM7)</c> suffix.</summary>
    public static string? ExtractComPort(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ComPortPattern.Match(text);
        return match.Success ? match.Groups["com"].Value.ToUpper(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Extracts the first <c>USB\VID_xxxx&amp;PID_yyyy</c> pair from hardware ids.</summary>
    public static (string? VendorId, string? ProductId) ExtractUsbIds(IReadOnlyList<string>? hardwareIds)
    {
        if (hardwareIds is null)
        {
            return (null, null);
        }

        foreach (var id in hardwareIds)
        {
            var match = UsbVidPidPattern.Match(id ?? string.Empty);
            if (match.Success)
            {
                return (match.Groups["vid"].Value, match.Groups["pid"].Value);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Extracts a device serial from the instance path's third segment. Segments that
    /// contain <c>&amp;</c> are hub-port location ids (bus topology), not device serials,
    /// so they are rejected to avoid inventing unstable "identities".
    /// </summary>
    public static string? ExtractInstanceSerial(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        var segments = instanceId.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3)
        {
            return null;
        }

        var candidate = segments[2];
        return candidate.Contains('&', StringComparison.Ordinal) ? null : candidate;
    }

    private static RawPortAvailability MapProbe(WindowsProbeResult probe) => probe switch
    {
        WindowsProbeResult.Available => RawPortAvailability.Available,
        WindowsProbeResult.InUse => RawPortAvailability.InUse,
        _ => RawPortAvailability.Unknown,
    };

    private static string? StripTrailingPortSuffix(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = ComPortPattern.Replace(name, string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorNoSuchDevice = 433;
}
