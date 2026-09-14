using System.Text;
using SerialScout.Core.Privacy;
using SerialScout.Core.Sessions;

namespace SerialScout.Core.Tests;

public sealed class LogRedactorTests
{
    public static TheoryData<string, RedactionPreset, string> FragmentedSecrets => new()
    {
        { "Bearer abc.def.ghi", RedactionPreset.Tokens, "abc.def.ghi" },
        { "api_key=super-secret-value", RedactionPreset.Tokens, "super-secret-value" },
        { "sk-proj-AbCdEf1234567890", RedactionPreset.Tokens, "AbCdEf1234567890" },
        { "abcdefgh.ijklmnop.qrstuvwx", RedactionPreset.Tokens, "abcdefgh.ijklmnop.qrstuvwx" },
        { "192.168.20.42", RedactionPreset.NetworkIdentifiers, "192.168.20.42" },
        { "2001:db8::42", RedactionPreset.NetworkIdentifiers, "2001:db8::42" },
        { "aa:bb:cc:dd:ee:ff", RedactionPreset.NetworkIdentifiers, "aa:bb:cc:dd:ee:ff" },
        { "/Users/alice/private/file.txt", RedactionPreset.FilePaths, "/Users/alice/private/file.txt" },
        { "C:\\Users\\alice\\private.txt", RedactionPreset.FilePaths, "C:\\Users\\alice\\private.txt" },
    };

    [Theory]
    [MemberData(nameof(FragmentedSecrets))]
    public void RedactsSecretsFragmentedAcrossAdjacentEvents(string secret, RedactionPreset preset, string leakedValue)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var middle = new byte[] { 0xA9, (byte)' ' }.Concat(secretBytes[..(secretBytes.Length / 2)]).ToArray();
        var tail = secretBytes[(secretBytes.Length / 2)..].Concat(" done"u8.ToArray()).ToArray();
        var events = new[]
        {
            new LogEvent(DateTimeOffset.UnixEpoch, LogEventDirection.Received, new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xC3 }),
            new LogEvent(DateTimeOffset.UnixEpoch.AddMilliseconds(1), LogEventDirection.Received, middle),
            new LogEvent(DateTimeOffset.UnixEpoch.AddMilliseconds(2), LogEventDirection.Received, tail),
        };

        var transformed = LogRedactor.Apply(events, preset);
        var text = string.Concat(transformed.Select(item => Encoding.UTF8.GetString(item.Payload)));

        Assert.Equal(events.Length, transformed.Count);
        Assert.Equal(events.Select(item => item.Utc), transformed.Select(item => item.Utc));
        Assert.Contains("café", text, StringComparison.Ordinal);
        Assert.DoesNotContain(leakedValue, text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesConsecutiveEventMetadataAndAssignsReplacementToFirstMatchedEvent()
    {
        var events = new[]
        {
            new LogEvent(DateTimeOffset.UnixEpoch.AddSeconds(1), LogEventDirection.Received, "before 192."u8.ToArray()),
            new LogEvent(DateTimeOffset.UnixEpoch.AddSeconds(10), LogEventDirection.Received, "168.20."u8.ToArray()),
            new LogEvent(DateTimeOffset.UnixEpoch.AddSeconds(20), LogEventDirection.Received, "42 after"u8.ToArray()),
        };

        var transformed = LogRedactor.Apply(events, RedactionPreset.NetworkIdentifiers);

        Assert.Equal(events.Length, transformed.Count);
        Assert.Equal(events.Select(item => item.Utc), transformed.Select(item => item.Utc));
        Assert.Equal(events.Select(item => item.Direction), transformed.Select(item => item.Direction));
        Assert.Equal("before [REDACTED:IP]", Encoding.UTF8.GetString(transformed[0].Payload));
        Assert.Empty(transformed[1].Payload);
        Assert.Equal(" after", Encoding.UTF8.GetString(transformed[2].Payload));
        Assert.Equal("before 192.", Encoding.UTF8.GetString(events[0].Payload));
        Assert.Equal("168.20.", Encoding.UTF8.GetString(events[1].Payload));
        Assert.Equal("42 after", Encoding.UTF8.GetString(events[2].Payload));
    }

    [Fact]
    public void DoesNotReconstructSecretsAcrossDirectionBoundaries()
    {
        var events = new[]
        {
            new LogEvent(DateTimeOffset.UnixEpoch, LogEventDirection.Received, "sk-proj-AbCdEf"u8.ToArray()),
            new LogEvent(DateTimeOffset.UnixEpoch, LogEventDirection.Sent, "1234567890"u8.ToArray()),
        };

        var transformed = LogRedactor.Apply(events, RedactionPreset.Tokens);

        Assert.Equal("sk-proj-AbCdEf", Encoding.UTF8.GetString(transformed[0].Payload));
        Assert.Equal("1234567890", Encoding.UTF8.GetString(transformed[1].Payload));
    }

    [Fact]
    public void ShareSafePresetRedactsSensitiveValuesWithoutMutatingOriginalEvents()
    {
        var originalBytes = Encoding.UTF8.GetBytes(
            "Authorization: Bearer abc.def.ghi from 192.168.1.20 aa:bb:cc:dd:ee:ff /Users/alice/project C:\\Users\\alice\\secret.txt");
        var events = new[]
        {
            new LogEvent(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero), LogEventDirection.Received, originalBytes),
        };

        var transformed = LogRedactor.Apply(events, RedactionPreset.ShareSafe);

        var redacted = SessionLogRenderer.Decode(Assert.Single(transformed).Payload);
        Assert.Contains("Bearer [REDACTED:TOKEN]", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:IP]", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:MAC]", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:PATH]", redacted, StringComparison.Ordinal);
        Assert.Equal(
            "Authorization: Bearer abc.def.ghi from 192.168.1.20 aa:bb:cc:dd:ee:ff /Users/alice/project C:\\Users\\alice\\secret.txt",
            Encoding.UTF8.GetString(originalBytes));
        Assert.NotSame(events[0].Payload, transformed[0].Payload);
    }

    [Fact]
    public void TokenPresetRedactsCommonStandaloneSecretShapes()
    {
        var events = new[]
        {
            new LogEvent(
                DateTimeOffset.UnixEpoch,
                LogEventDirection.Sent,
                Encoding.UTF8.GetBytes("sk-proj-AbCdEf1234567890 ghp_1234567890abcdefghijklmnop xoxb-1234567890-secret")),
        };

        var text = SessionLogRenderer.Decode(Assert.Single(LogRedactor.Apply(events, RedactionPreset.Tokens)).Payload);

        Assert.Equal("[REDACTED:TOKEN] [REDACTED:TOKEN] [REDACTED:TOKEN]", text);
    }

    [Fact]
    public void NetworkPresetRedactsCompressedIpv6Addresses()
    {
        var events = new[]
        {
            new LogEvent(DateTimeOffset.UnixEpoch, LogEventDirection.Received, Encoding.UTF8.GetBytes("peer=2001:db8::1 loopback=::1")),
        };

        var text = SessionLogRenderer.Decode(Assert.Single(LogRedactor.Apply(events, RedactionPreset.NetworkIdentifiers)).Payload);

        Assert.Equal("peer=[REDACTED:IP] loopback=[REDACTED:IP]", text);
    }
}
