namespace SerialScout.Core.Discovery;

/// <summary>
/// Raw platform-flavoured port record handed to <see cref="PortNormalizer"/> by an adapter.
/// Strings are kept exactly as the operating system reported them (or <c>null</c> when the
/// OS did not expose the field); parsing and state mapping happen in the normalizer.
/// </summary>
/// <param name="PortPath">OS port path, e.g. <c>COM7</c> or <c>/dev/cu.usbserial-110</c>.</param>
/// <param name="DisplayName">Raw device display name when available.</param>
/// <param name="VendorId">Raw vendor id text (hex, with or without <c>0x</c> prefix).</param>
/// <param name="ProductId">Raw product id text (hex, with or without <c>0x</c> prefix).</param>
/// <param name="Manufacturer">Raw manufacturer string when available.</param>
/// <param name="Product">Raw product string when available.</param>
/// <param name="SerialNumber">Raw serial number string when available.</param>
/// <param name="Availability">Raw availability/error signal from the platform probe.</param>
/// <param name="Notes">Optional adapter note carried through to the normalized port.</param>
public sealed record RawPortRecord(
    string PortPath,
    string? DisplayName = null,
    string? VendorId = null,
    string? ProductId = null,
    string? Manufacturer = null,
    string? Product = null,
    string? SerialNumber = null,
    RawPortAvailability Availability = RawPortAvailability.Unknown,
    string? Notes = null);

/// <summary>
/// Raw availability signal a platform adapter observed for a port.
/// </summary>
public enum RawPortAvailability
{
    /// <summary>Not probed or not determinable; maps to <see cref="ScanState.Unknown"/>.</summary>
    Unknown,

    /// <summary>Port appears free; maps to <see cref="ScanState.Ready"/>.</summary>
    Available,

    /// <summary>Port is held by another process; maps to <see cref="ScanState.Busy"/>.</summary>
    InUse,

    /// <summary>OS refused access; maps to <see cref="ScanState.PermissionDenied"/>.</summary>
    AccessDenied,
}
