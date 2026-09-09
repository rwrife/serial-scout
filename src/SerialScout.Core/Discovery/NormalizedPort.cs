namespace SerialScout.Core.Discovery;

/// <summary>
/// A normalized view of one serial port produced by a platform discovery adapter.
/// Unknown or missing metadata is represented as <c>null</c>; adapters never invent values.
/// </summary>
/// <param name="PortPath">OS port path, e.g. <c>COM7</c> or <c>/dev/cu.usbserial-110</c>.</param>
/// <param name="State">Normalized scan state for this port.</param>
/// <param name="VendorId">USB vendor id when the OS exposes it.</param>
/// <param name="ProductId">USB product id when the OS exposes it.</param>
/// <param name="Manufacturer">Manufacturer string when the OS exposes it.</param>
/// <param name="Product">Product name when the OS exposes it.</param>
/// <param name="SerialNumber">Device serial number when the OS exposes it.</param>
/// <param name="DisplayName">Human-readable device name when the OS exposes one.</param>
/// <param name="Notes">Optional adapter note explaining degraded metadata or state.</param>
public sealed record NormalizedPort(
    string PortPath,
    ScanState State,
    int? VendorId = null,
    int? ProductId = null,
    string? Manufacturer = null,
    string? Product = null,
    string? SerialNumber = null,
    string? DisplayName = null,
    string? Notes = null);
