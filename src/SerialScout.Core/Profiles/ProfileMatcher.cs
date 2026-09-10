using SerialScout.Core.Discovery;

namespace SerialScout.Core.Profiles;

/// <summary>
/// Deterministic, explainable identity matching from a discovered <see cref="NormalizedPort"/>
/// to locally stored <see cref="DeviceProfile"/>s.
///
/// Rules are deliberately conservative:
/// <list type="bullet">
/// <item>The required gate is an exact VID/PID equality — nothing matches without it.</item>
/// <item>Evidence that directly contradicts the port (a pinned serial that differs, or a hint
/// that is absent from a present product/manufacturer string) rejects the candidate outright.</item>
/// <item>An <see cref="MatchConfidence.Exact"/> binding is only produced for a unique top-scoring
/// candidate whose pinned serial fingerprint was positively verified against the port and whose
/// profile was seen within the staleness window. Everything else is surfaced as
/// <see cref="MatchConfidence.Ambiguous"/> with reasons.</item>
/// </list>
///
/// The matcher performs no IO; it is a pure function of (port, profiles, now) and is fully
/// unit-testable on any host.
/// </summary>
public sealed class ProfileMatcher
{
    /// <summary>Score awarded for passing the required VID/PID gate.</summary>
    public const int ScoreVendorProduct = 10;

    /// <summary>Score awarded when a profile-pinned serial fingerprint was verified against the port.</summary>
    public const int ScoreSerialVerified = 8;

    /// <summary>Score awarded when a product hint was found inside the port's reported product string.</summary>
    public const int ScoreProductHint = 2;

    /// <summary>Score awarded when a manufacturer hint was found inside the port's reported manufacturer.</summary>
    public const int ScoreManufacturerHint = 2;

    /// <summary>Score awarded when the profile was seen within the staleness window.</summary>
    public const int ScoreRecentlySeen = 1;

    /// <summary>Default window after which a profile stops counting as "recently seen".</summary>
    public static readonly TimeSpan DefaultStalenessWindow = TimeSpan.FromDays(45);

    /// <summary>Candidate reason: profile passed the required VID/PID gate.</summary>
    public const string ReasonVendorProduct = "vendor-product-match";

    /// <summary>Candidate reason: pinned serial fingerprint verified against the port serial.</summary>
    public const string ReasonSerialVerified = "serial-verified";

    /// <summary>Candidate reason: profile pins a serial but the port did not report one.</summary>
    public const string ReasonSerialUnverified = "serial-unverified";

    /// <summary>Candidate reason: product hint matched the port's product string.</summary>
    public const string ReasonProductHint = "product-hint-match";

    /// <summary>Candidate reason: manufacturer hint matched the port's manufacturer string.</summary>
    public const string ReasonManufacturerHint = "manufacturer-hint-match";

    /// <summary>Candidate reason: profile was last seen within the staleness window.</summary>
    public const string ReasonRecentlySeen = "recently-seen";

    /// <summary>Candidate reason: profile was last seen outside the staleness window.</summary>
    public const string ReasonProfileStale = "profile-stale";

    /// <summary>Candidate reason: profile has never been seen by the matcher.</summary>
    public const string ReasonProfileNeverSeen = "profile-never-seen";

    /// <summary>Result reason: the port metadata lacks a vendor or product id, so nothing can be judged.</summary>
    public const string ResultMissingPortIdentity = "missing-vendor-or-product-id";

    /// <summary>Result reason: no profile survived the gates.</summary>
    public const string ResultNoCandidates = "no-candidates";

    /// <summary>Result reason: exactly one candidate held the top score.</summary>
    public const string ResultUniqueTopCandidate = "unique-top-candidate";

    /// <summary>Result reason: two or more candidates tied at the top score.</summary>
    public const string ResultScoreTieAtTop = "score-tie-at-top";

    /// <summary>Result reason: the top candidate's serial fingerprint was not positively verified.</summary>
    public const string ResultTopCandidateUnverified = "top-candidate-not-serial-verified";

    /// <summary>Result reason: the top candidate is stale or has never been seen.</summary>
    public const string ResultTopCandidateStale = "top-candidate-stale";

    private static readonly string[] MissingPortIdentityReasons = new[] { ResultMissingPortIdentity };
    private static readonly string[] NoCandidatesReasons = new[] { ResultNoCandidates };

    private readonly TimeSpan _stalenessWindow;

    /// <summary>
    /// Creates a matcher.
    /// </summary>
    /// <param name="stalenessWindow">Override for the recency window; defaults to <see cref="DefaultStalenessWindow"/>.</param>
    public ProfileMatcher(TimeSpan? stalenessWindow = null)
    {
        var window = stalenessWindow ?? DefaultStalenessWindow;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _stalenessWindow = window;
    }

    /// <summary>
    /// Matches one discovered port against the known profiles at <paramref name="nowUtc"/>.
    /// The candidate list is ordered by score descending, then profile id ascending, so the
    /// output is fully deterministic for identical inputs.
    /// </summary>
    /// <param name="port">Normalized discovered port to identify.</param>
    /// <param name="profiles">Locally stored profiles to match against.</param>
    /// <param name="nowUtc">Current UTC time; injected so scoring is deterministic and testable.</param>
    /// <returns>An explainable <see cref="MatchResult"/>.</returns>
    public MatchResult Match(NormalizedPort port, IEnumerable<DeviceProfile> profiles, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(profiles);

        if (port.VendorId is not int vendorId || port.ProductId is not int productId)
        {
            return new MatchResult(MatchConfidence.Unknown, null, Array.Empty<RankedMatch>(), MissingPortIdentityReasons);
        }

        var candidates = new List<RankedMatch>();
        foreach (var profile in profiles)
        {
            var scored = TryScore(profile, vendorId, productId, port, nowUtc);
            if (scored is not null)
            {
                candidates.Add(scored);
            }
        }

        if (candidates.Count == 0)
        {
            return new MatchResult(MatchConfidence.Unknown, null, Array.Empty<RankedMatch>(), NoCandidatesReasons);
        }

        candidates.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Profile.Id.CompareTo(right.Profile.Id);
        });

        var top = candidates[0];
        var tieAtTop = candidates.Count > 1 && candidates[1].Score == top.Score;
        var serialVerified = top.Reasons.Contains(ReasonSerialVerified, StringComparer.Ordinal);
        var stale = top.Reasons.Contains(ReasonProfileStale, StringComparer.Ordinal)
            || top.Reasons.Contains(ReasonProfileNeverSeen, StringComparer.Ordinal);

        var resultReasons = new List<string>(3);
        if (tieAtTop)
        {
            resultReasons.Add(ResultScoreTieAtTop);
        }
        else
        {
            resultReasons.Add(ResultUniqueTopCandidate);
        }

        if (!serialVerified)
        {
            resultReasons.Add(ResultTopCandidateUnverified);
        }

        if (stale)
        {
            resultReasons.Add(ResultTopCandidateStale);
        }

        var exact = !tieAtTop && serialVerified && !stale;
        return new MatchResult(
            exact ? MatchConfidence.Exact : MatchConfidence.Ambiguous,
            exact ? top.Profile : null,
            candidates,
            resultReasons);
    }

    private RankedMatch? TryScore(
        DeviceProfile profile,
        int vendorId,
        int productId,
        NormalizedPort port,
        DateTimeOffset nowUtc)
    {
        var rule = profile.Rule;
        if (rule.VendorId != vendorId || rule.ProductId != productId)
        {
            return null;
        }

        var reasons = new List<string>(6) { ReasonVendorProduct };
        var score = ScoreVendorProduct;

        if (rule.SerialFingerprint is string fingerprint)
        {
            if (port.SerialNumber is string serial)
            {
                if (!string.Equals(serial, fingerprint, StringComparison.Ordinal))
                {
                    return null;
                }

                score += ScoreSerialVerified;
                reasons.Add(ReasonSerialVerified);
            }
            else
            {
                reasons.Add(ReasonSerialUnverified);
            }
        }

        if (rule.ProductHint is string productHint)
        {
            if (port.Product is string product)
            {
                if (!product.Contains(productHint, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                score += ScoreProductHint;
                reasons.Add(ReasonProductHint);
            }
        }

        if (rule.ManufacturerHint is string manufacturerHint)
        {
            if (port.Manufacturer is string manufacturer)
            {
                if (!manufacturer.Contains(manufacturerHint, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                score += ScoreManufacturerHint;
                reasons.Add(ReasonManufacturerHint);
            }
        }

        var recentlySeen = profile.LastSeenUtc is DateTimeOffset lastSeen
            && nowUtc - lastSeen <= _stalenessWindow;
        if (recentlySeen)
        {
            score += ScoreRecentlySeen;
            reasons.Add(ReasonRecentlySeen);
        }
        else if (profile.LastSeenUtc is null)
        {
            reasons.Add(ReasonProfileNeverSeen);
        }
        else
        {
            reasons.Add(ReasonProfileStale);
        }

        return new RankedMatch(profile, score, reasons);
    }
}
