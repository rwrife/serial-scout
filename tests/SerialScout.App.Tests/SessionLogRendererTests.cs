using System.Text;
using SerialScout.Core.Sessions;

namespace SerialScout.App.Tests;

/// <summary>
/// Tests for the pure log renderer used by the terminal log panel (issue #5):
/// stable invariant timestamps, text RX/TX direction badges, filtering, and payload
/// decoding rules.
/// </summary>
public sealed class SessionLogRendererTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 12, 14, 3, 5, TimeSpan.Zero);

    private static readonly byte[] HelloBytes = "hello"u8.ToArray();
    private static readonly byte[] WorldBytes = "world"u8.ToArray();
    private static readonly byte[] AtBytes = "AT\r\n"u8.ToArray();
    private static readonly byte[] PartialEmoji = new byte[] { (byte)'a', 0xF0, 0x9F, 0x99 }; // 'a' + truncated 4-byte emoji

    [Fact]
    public void RendersStableLinesWithDirectionTextBadges()
    {
        var events = new[]
        {
            new LogEvent(BaseUtc, LogEventDirection.Received, HelloBytes),
            new LogEvent(BaseUtc.AddSeconds(2), LogEventDirection.Sent, AtBytes),
        };

        var rendered = SessionLogRenderer.Render(events);

        Assert.Equal("14:03:05.000 RX hello\n14:03:07.000 TX AT", rendered);
    }

    [Fact]
    public void FilterIsCaseInsensitiveAndMatchesPayloadOnly()
    {
        var events = new[]
        {
            new LogEvent(BaseUtc, LogEventDirection.Received, HelloBytes),
            new LogEvent(BaseUtc, LogEventDirection.Sent, WorldBytes),
        };

        var rendered = SessionLogRenderer.Render(events, "WORLD");

        Assert.Equal("14:03:05.000 TX world", rendered);
    }

    [Fact]
    public void FilterNeverMatchesTimestampOrDirectionPrefix()
    {
        var events = new[] { new LogEvent(BaseUtc, LogEventDirection.Received, HelloBytes) };

        Assert.Equal(string.Empty, SessionLogRenderer.Render(events, "14:03"));
        Assert.Equal(string.Empty, SessionLogRenderer.Render(events, "RX"));
    }

    [Fact]
    public void BlankFilterDisablesFiltering()
    {
        var events = new[] { new LogEvent(BaseUtc, LogEventDirection.Received, HelloBytes) };

        Assert.Equal("14:03:05.000 RX hello", SessionLogRenderer.Render(events, "   "));
    }

    [Fact]
    public void TrailingLineEndingsAreTrimmedButInteriorNewlinesSurvive()
    {
        var events = new[]
        {
            new LogEvent(BaseUtc, LogEventDirection.Received, "a\r\nb\r\n"u8.ToArray()),
        };

        Assert.Equal("14:03:05.000 RX a\r\nb", SessionLogRenderer.Render(events));
    }

    [Fact]
    public void PartialMultiByteSequencesUseReplacementWithoutDroppingEvents()
    {
        var events = new[]
        {
            new LogEvent(BaseUtc, LogEventDirection.Received, PartialEmoji),
            new LogEvent(BaseUtc, LogEventDirection.Received, HelloBytes),
        };

        var rendered = SessionLogRenderer.Render(events);

        Assert.Contains("a\uFFFD", rendered, StringComparison.Ordinal);
        Assert.Contains("RX hello", rendered, StringComparison.Ordinal);
        Assert.Equal(2, rendered.Split('\n').Length);
    }

    [Fact]
    public void DecodeMatchesUtf8Semantics()
        => Assert.Equal("ok \u00e9", SessionLogRenderer.Decode("ok é"u8.ToArray()));

    [Fact]
    public void EmptySnapshotRendersEmpty()
        => Assert.Equal(string.Empty, SessionLogRenderer.Render(Array.Empty<LogEvent>()));

    [Fact]
    public void RendererDoesNotMutatePayloads()
    {
        var payload = "keep"u8.ToArray();
        SessionLogRenderer.Render(new[] { new LogEvent(BaseUtc, LogEventDirection.Received, payload) });

        Assert.Equal("keep"u8.ToArray(), payload);
        _ = Encoding.UTF8; // pins the encoding dependency for clarity
    }
}
