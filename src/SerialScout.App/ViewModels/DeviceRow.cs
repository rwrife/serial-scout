using System.Globalization;
using SerialScout.Core.Discovery;
using SerialScout.Core.Profiles;

namespace SerialScout.App.ViewModels;

/// <summary>
/// One discovered device rendered for the device list. Every status signal is text:
/// the state badge carries a symbol + word (never color alone) so screen readers and
/// monochrome displays convey identical information.
/// </summary>
public sealed class DeviceRow : ViewModelBase
{
    /// <summary>Creates a row over a discovered port and its (optional) profile match.</summary>
    public DeviceRow(NormalizedPort port, MatchResult match)
    {
        Port = port;
        Match = match;
        MatchedProfileName = match.Profile?.Name;
    }

    /// <summary>The underlying normalized port (binding surface stays textual).</summary>
    public NormalizedPort Port { get; }

    /// <summary>Explainable profile-match outcome for this port.</summary>
    public MatchResult Match { get; }

    /// <summary>OS port path, e.g. <c>COM7</c>.</summary>
    public string PortPath => Port.PortPath;

    /// <summary>Text badge for the scan state, never color-only.</summary>
    public string StateLabel => Port.State switch
    {
        ScanState.Ready => "[ok] Ready",
        ScanState.Busy => "[busy] In use",
        ScanState.PermissionDenied => "[denied] Permission denied",
        _ => "[?] Unknown",
    };

    /// <summary>USB identity as <c>VID:PID</c> hex, or <c>no usb id</c>.</summary>
    public string IdentityLabel => Port.VendorId is int vendor && Port.ProductId is int product
        ? $"{vendor.ToString("X4", CultureInfo.InvariantCulture)}:{product.ToString("X4", CultureInfo.InvariantCulture)}"
        : "no usb id";

    /// <summary>Human description: product / manufacturer / serial when the OS exposed them.</summary>
    public string DescriptionLabel
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrWhiteSpace(Port.Product))
            {
                parts.Add(Port.Product);
            }

            if (!string.IsNullOrWhiteSpace(Port.Manufacturer))
            {
                parts.Add(Port.Manufacturer);
            }

            if (!string.IsNullOrWhiteSpace(Port.SerialNumber))
            {
                parts.Add($"s/n {Port.SerialNumber}");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : Port.PortPath;
        }
    }

    /// <summary>Profile bound with exact confidence, or <see langword="null"/>.</summary>
    public string? MatchedProfileName { get; }

    /// <summary>Match confidence rendered as text.</summary>
    public string MatchLabel => Match.Confidence switch
    {
        MatchConfidence.Exact => $"profile: {Match.Profile?.Name} (exact)",
        MatchConfidence.Ambiguous => "profile: uncertain match",
        _ => "profile: unknown device",
    };

    /// <summary>Screen-reader-friendly one-line summary of the whole row.</summary>
    public string AccessibilitySummary =>
        $"{PortPath}, {StateLabel}, {IdentityLabel}, {MatchLabel}";
}
