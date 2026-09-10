namespace SerialScout.Core.Profiles;

/// <summary>
/// Identity rule for a device profile. VID/PID are required because they are the only
/// identity fields both supported platforms report reliably; serial fingerprints and
/// product/manufacturer hints are optional strengthening evidence used by
/// <see cref="ProfileMatcher"/> and are never invented when the OS does not expose them.
/// </summary>
public sealed record ProfileMatchRule
{
    /// <summary>
    /// Creates a match rule. Optional text fingerprints are trimmed and collapsed to
    /// <see langword="null"/> when blank so stored rules never hold whitespace-only hints.
    /// </summary>
    /// <param name="vendorId">USB vendor id (0..0xFFFF).</param>
    /// <param name="productId">USB product id (0..0xFFFF).</param>
    /// <param name="serialFingerprint">Exact serial-number fingerprint for strong binding.</param>
    /// <param name="productHint">Substring hint matched case-insensitively against the product name.</param>
    /// <param name="manufacturerHint">Substring hint matched case-insensitively against the manufacturer.</param>
    public ProfileMatchRule(
        int vendorId,
        int productId,
        string? serialFingerprint = null,
        string? productHint = null,
        string? manufacturerHint = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vendorId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vendorId, 0xFFFF);
        ArgumentOutOfRangeException.ThrowIfNegative(productId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(productId, 0xFFFF);

        VendorId = vendorId;
        ProductId = productId;
        SerialFingerprint = Clean(serialFingerprint);
        ProductHint = Clean(productHint);
        ManufacturerHint = Clean(manufacturerHint);
    }

    /// <summary>Required USB vendor id.</summary>
    public int VendorId { get; }

    /// <summary>Required USB product id.</summary>
    public int ProductId { get; }

    /// <summary>Exact serial-number fingerprint, or <see langword="null"/> when the profile does not pin one.</summary>
    public string? SerialFingerprint { get; }

    /// <summary>Optional case-insensitive product-name hint.</summary>
    public string? ProductHint { get; }

    /// <summary>Optional case-insensitive manufacturer hint.</summary>
    public string? ManufacturerHint { get; }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
