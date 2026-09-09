using System.Globalization;

namespace SerialScout.Core.Discovery;

/// <summary>
/// Pure normalization from raw OS-flavoured port records to <see cref="NormalizedPort"/>.
/// Parsing is deliberately conservative: anything unparseable becomes <c>null</c> so
/// downstream identity matching never invents values it did not actually receive.
/// </summary>
public static class PortNormalizer
{
    /// <summary>Note tag added when a raw vendor id was present but unparseable.</summary>
    public const string NoteVendorIdUnparsed = "vendor-id-unparsed";

    /// <summary>Note tag added when a raw product id was present but unparseable.</summary>
    public const string NoteProductIdUnparsed = "product-id-unparsed";

    /// <summary>Normalizes one raw record, mapping availability to an explicit <see cref="ScanState"/>.</summary>
    public static NormalizedPort Normalize(RawPortRecord raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var portPath = (raw.PortPath ?? string.Empty).Trim();
        if (portPath.Length == 0)
        {
            throw new ArgumentException("Raw port records must carry a non-empty port path.", nameof(raw));
        }

        var diagnostics = new List<string>(2);

        var vendorId = ParseUsbId(raw.VendorId);
        if (vendorId is null && HasText(raw.VendorId))
        {
            diagnostics.Add(NoteVendorIdUnparsed);
        }

        var productId = ParseUsbId(raw.ProductId);
        if (productId is null && HasText(raw.ProductId))
        {
            diagnostics.Add(NoteProductIdUnparsed);
        }

        return new NormalizedPort(
            portPath,
            MapState(raw.Availability),
            vendorId,
            productId,
            Clean(raw.Manufacturer),
            Clean(raw.Product),
            Clean(raw.SerialNumber),
            Clean(raw.DisplayName),
            MergeNotes(raw.Notes, diagnostics));
    }

    /// <summary>Normalizes many records and orders them deterministically with <see cref="PortPathComparer"/>.</summary>
    public static IReadOnlyList<NormalizedPort> NormalizeAll(IEnumerable<RawPortRecord> raws)
    {
        ArgumentNullException.ThrowIfNull(raws);
        return raws
            .Select(Normalize)
            .OrderBy(p => p.PortPath, PortPathComparer.Natural)
            .ToList();
    }

    /// <summary>Maps a raw availability signal to the explicit per-port scan state model.</summary>
    public static ScanState MapState(RawPortAvailability availability) => availability switch
    {
        RawPortAvailability.Available => ScanState.Ready,
        RawPortAvailability.InUse => ScanState.Busy,
        RawPortAvailability.AccessDenied => ScanState.PermissionDenied,
        _ => ScanState.Unknown,
    };

    /// <summary>
    /// Parses a USB identifier conservatively. Accepts optional <c>0x</c>, <c>VID_</c>, and
    /// <c>PID_</c> prefixes and requires the remainder to be in-range hexadecimal.
    /// Returns <c>null</c> for missing or malformed values.
    /// </summary>
    public static int? ParseUsbId(string? raw)
    {
        var text = Clean(raw);
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        else if (text.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            || value is < 0 or > 0xFFFF)
        {
            return null;
        }

        return value;
    }

    private static string? MergeNotes(string? baseNote, IReadOnlyList<string> diagnostics)
    {
        var parts = new List<string>(diagnostics.Count + 1);
        var trimmedBase = Clean(baseNote);
        if (trimmedBase is not null)
        {
            parts.Add(trimmedBase);
        }

        parts.AddRange(diagnostics);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
}
