namespace SerialScout.Core.Profiles;

/// <summary>
/// Outcome confidence for a profile match. Uncertain outcomes are surfaced explicitly;
/// the system never auto-binds an uncertain match as <see cref="Exact"/>.
/// </summary>
public enum MatchConfidence
{
    /// <summary>
    /// Strongly verified identity: VID/PID plus a verified serial fingerprint, a unique
    /// top-scoring candidate, and a profile seen within the staleness window.
    /// </summary>
    Exact,

    /// <summary>
    /// One or more plausible candidates, but at least one is not independently verified
    /// (hint-only evidence, missing serial, a score tie, or a stale profile).
    /// </summary>
    Ambiguous,

    /// <summary>No candidate survived the required VID/PID gate, or the device metadata is too thin to judge.</summary>
    Unknown,
}

/// <summary>
/// Explainable result of <see cref="ProfileMatcher.Match"/>: the confidence, the profile
/// safe to auto-bind (only for <see cref="MatchConfidence.Exact"/>), every surviving
/// candidate with its score and reasons, plus result-level tags explaining the final
/// confidence decision.
/// </summary>
/// <param name="Confidence">Outcome confidence for this match.</param>
/// <param name="Profile">The profile safe to auto-bind; non-null only for <see cref="MatchConfidence.Exact"/>.</param>
/// <param name="Candidates">Surviving candidate profiles ordered by score, best first.</param>
/// <param name="Reasons">Stable result-level tags explaining why this confidence was chosen.</param>
public sealed record MatchResult(
    MatchConfidence Confidence,
    DeviceProfile? Profile,
    IReadOnlyList<RankedMatch> Candidates,
    IReadOnlyList<string> Reasons);

/// <summary>
/// One surviving candidate with its deterministic score and the reasons that produced it.
/// </summary>
/// <param name="Profile">The candidate profile.</param>
/// <param name="Score">Sum of <see cref="ProfileMatcher"/> score constants for the evidence observed.</param>
/// <param name="Reasons">Stable reason tags explaining the score, in evidence order.</param>
public sealed record RankedMatch(
    DeviceProfile Profile,
    int Score,
    IReadOnlyList<string> Reasons);
