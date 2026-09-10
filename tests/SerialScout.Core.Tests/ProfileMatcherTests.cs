using SerialScout.Core.Discovery;
using SerialScout.Core.Profiles;

namespace SerialScout.Core.Tests;

public sealed class ProfileMatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ExpectedDeterministicOrder = new[] { "a", "b", "c" };

    [Fact]
    public void ExactMatchRequiresUniqueVerifiedSerialAndRecentSighting()
    {
        var profile = Profile(1, name: "esp32", serial: "ABC123", lastSeenUtc: Now - TimeSpan.FromDays(1));
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, serial: "ABC123");

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Exact, result.Confidence);
        Assert.NotNull(result.Profile);
        Assert.Equal("esp32", result.Profile!.Name);
        Assert.Single(result.Candidates);
        Assert.Contains(ProfileMatcher.ReasonSerialVerified, result.Candidates[0].Reasons);
        Assert.Contains(ProfileMatcher.ResultUniqueTopCandidate, result.Reasons);
    }

    [Fact]
    public void HintOnlyEvidenceIsNeverExact()
    {
        var profile = Profile(1, name: "wch-board", productHint: "USB Serial", lastSeenUtc: Now);
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, productName: "USB Serial Device");

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Ambiguous, result.Confidence);
        Assert.Null(result.Profile);
        Assert.Contains(ProfileMatcher.ResultTopCandidateUnverified, result.Reasons);
    }

    [Fact]
    public void ScoreTieAtTopIsAmbiguousEvenWithTwoVerifiedProfiles()
    {
        var first = Profile(1, name: "first", serial: "ABC123", lastSeenUtc: Now);
        var second = Profile(2, name: "second", serial: "ABC123", lastSeenUtc: Now);
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, serial: "ABC123");

        var result = new ProfileMatcher().Match(port, new[] { first, second }, Now);

        Assert.Equal(MatchConfidence.Ambiguous, result.Confidence);
        Assert.Null(result.Profile);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("first", result.Candidates[0].Profile.Name);
        Assert.Contains(ProfileMatcher.ResultScoreTieAtTop, result.Reasons);
    }

    [Fact]
    public void VerifiedProfileBeatsStaleUnverifiedRivalOnTieBreak()
    {
        var verified = Profile(2, name: "known", serial: "ABC123", lastSeenUtc: Now);
        var staleHint = Profile(1, name: "old-hint", productHint: "Serial", lastSeenUtc: Now - TimeSpan.FromDays(365));
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, serial: "ABC123", productName: "USB Serial Device");

        var result = new ProfileMatcher().Match(port, new[] { verified, staleHint }, Now);

        Assert.Equal(MatchConfidence.Exact, result.Confidence);
        Assert.Equal("known", result.Profile!.Name);
        Assert.Equal(2, result.Candidates.Count);
        Assert.True(result.Candidates[0].Score > result.Candidates[1].Score);
    }

    [Fact]
    public void StaleProfileNeverAutoBinds()
    {
        var profile = Profile(1, name: "dusty", serial: "ABC123", lastSeenUtc: Now - TimeSpan.FromDays(90));
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, serial: "ABC123");

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Ambiguous, result.Confidence);
        Assert.Null(result.Profile);
        Assert.Contains(ProfileMatcher.ReasonProfileStale, result.Candidates[0].Reasons);
        Assert.Contains(ProfileMatcher.ResultTopCandidateStale, result.Reasons);
    }

    [Fact]
    public void NeverSeenProfileCannotBeExact()
    {
        var profile = Profile(1, name: "new", serial: "ABC123", lastSeenUtc: null);
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, serial: "ABC123");

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Ambiguous, result.Confidence);
        Assert.Null(result.Profile);
        Assert.Contains(ProfileMatcher.ReasonProfileNeverSeen, result.Candidates[0].Reasons);
    }

    [Fact]
    public void ContradictingSerialRejectsCandidate()
    {
        var profile = Profile(1, name: "wrong-serial", serial: "ZZZ999", lastSeenUtc: Now);
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, serial: "ABC123");

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Unknown, result.Confidence);
        Assert.Empty(result.Candidates);
        Assert.Contains(ProfileMatcher.ResultNoCandidates, result.Reasons);
    }

    [Fact]
    public void ContradictingProductHintRejectsCandidate()
    {
        var profile = Profile(1, name: "wrong-hint", productHint: "CH340", lastSeenUtc: Now);
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, productName: "CP2102 USB to UART");

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Unknown, result.Confidence);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void PortWithoutUsbIdentityIsUnknown()
    {
        var profile = Profile(1, name: "any", serial: "ABC123", lastSeenUtc: Now);
        var port = new NormalizedPort("/dev/cu.usbserial-110", ScanState.Unknown);

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Unknown, result.Confidence);
        Assert.Contains(ProfileMatcher.ResultMissingPortIdentity, result.Reasons);
    }

    [Fact]
    public void MissingSerialOnPortDemotesToAmbiguous()
    {
        var profile = Profile(1, name: "esp32", serial: "ABC123", lastSeenUtc: Now);
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, serial: null);

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Ambiguous, result.Confidence);
        Assert.Null(result.Profile);
        Assert.Contains(ProfileMatcher.ReasonSerialUnverified, result.Candidates[0].Reasons);
    }

    [Fact]
    public void DifferentVidPidIsNoMatch()
    {
        var profile = Profile(1, name: "other", serial: "ABC123", lastSeenUtc: Now);
        var port = Port("COM7", vendor: 0x10C4, product: 0xEA60, serial: "ABC123");

        var result = new ProfileMatcher().Match(port, new[] { profile }, Now);

        Assert.Equal(MatchConfidence.Unknown, result.Confidence);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ScoringIsDeterministicForSameInputs()
    {
        var profiles = new[]
        {
            Profile(3, name: "c", productHint: "Serial", lastSeenUtc: Now),
            Profile(1, name: "a", productHint: "Serial", lastSeenUtc: Now),
            Profile(2, name: "b", productHint: "Serial", lastSeenUtc: Now),
        };
        var port = Port("COM7", vendor: 0x1A86, product: 0x7523, productName: "USB Serial Device");
        var matcher = new ProfileMatcher();

        var first = matcher.Match(port, profiles, Now);
        var second = matcher.Match(port, profiles.Reverse(), Now);

        Assert.Equal(
            first.Candidates.Select(c => c.Profile.Name),
            second.Candidates.Select(c => c.Profile.Name));
        Assert.Equal(ExpectedDeterministicOrder, first.Candidates.Select(c => c.Profile.Name));
    }

    private static DeviceProfile Profile(
        long id,
        string name,
        string? serial = null,
        string? productHint = null,
        DateTimeOffset? lastSeenUtc = null)
        => new()
        {
            Id = id,
            Name = name,
            Rule = new ProfileMatchRule(0x1A86, 0x7523, serialFingerprint: serial, productHint: productHint),
            LastSeenUtc = lastSeenUtc,
        };

    private static NormalizedPort Port(
        string path,
        int vendor,
        int product,
        string? serial = null,
        string? productName = null)
        => new(
            path,
            ScanState.Ready,
            VendorId: vendor,
            ProductId: product,
            SerialNumber: serial,
            Product: productName);
}
