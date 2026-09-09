namespace SerialScout.Core.Discovery.Macos;

/// <summary>
/// A serial client extracted from raw <c>ioreg</c> output, with USB identity metadata
/// gathered from its IOKit ancestry. Numeric ids are stored as <c>0x</c>-prefixed hex so
/// they normalize consistently across platforms.
/// </summary>
/// <param name="PortPath">Callout device path, e.g. <c>/dev/cu.usbmodem101</c>.</summary>
/// <param name="TTYDevice">BSD tty base name, e.g. <c>usbmodem101</c>.</param>
/// <param name="VendorIdHex">Ancestor <c>idVendor</c> as hex text when present.</param>
/// <param name="ProductIdHex">Ancestor <c>idProduct</c> as hex text when present.</param>
/// <param name="Manufacturer">Ancestor <c>USB Vendor Name</c> when present.</param>
/// <param name="Product">Ancestor <c>USB Product Name</c> when present.</param>
/// <param name="SerialNumber">Ancestor <c>iSerialNumber</c> when present.</param>
/// <param name="ClassName">IOKit class of the ancestor the identity came from, if any.</param>
public sealed record MacosSerialEntry(
    string PortPath,
    string? TTYDevice,
    string? VendorIdHex,
    string? ProductIdHex,
    string? Manufacturer,
    string? Product,
    string? SerialNumber,
    string? ClassName);
