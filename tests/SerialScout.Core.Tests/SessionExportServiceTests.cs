using System.Text;
using SerialScout.Core.Privacy;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;

namespace SerialScout.Core.Tests;

public sealed class SessionExportServiceTests
{
    private static readonly SessionMetadata Session = new()
    {
        Id = 42,
        ProfileId = 7,
        PortPath = "/dev/ttyUSB0",
        StartedUtc = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
        EndedUtc = new DateTimeOffset(2026, 9, 14, 12, 1, 0, TimeSpan.Zero),
        Notes = "boot check",
    };

    private static readonly LogEvent[] Events =
    [
        new(new DateTimeOffset(2026, 9, 14, 12, 0, 1, TimeSpan.Zero), LogEventDirection.Received, Encoding.UTF8.GetBytes("ip=10.0.0.8\n")),
        new(new DateTimeOffset(2026, 9, 14, 12, 0, 2, TimeSpan.Zero), LogEventDirection.Sent, Encoding.UTF8.GetBytes("status\r\n")),
    ];

    [Fact]
    public async Task JsonPreviewIsDeterministicAndRequiresExplicitConfirmationBeforeWrite()
    {
        var preview = SessionExportService.CreatePreview(Session, Events, RedactionPreset.NetworkIdentifiers, SessionExportFormat.Json);
        var second = SessionExportService.CreatePreview(Session, Events, RedactionPreset.NetworkIdentifiers, SessionExportFormat.Json);

        Assert.Equal(preview.Content, second.Content);
        Assert.Contains("\"formatVersion\": 1", preview.Content, StringComparison.Ordinal);
        Assert.Contains("\"direction\": \"received\"", preview.Content, StringComparison.Ordinal);
        Assert.Contains("ip=[REDACTED:IP]\\n", preview.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.8", preview.Content, StringComparison.Ordinal);

        var path = Path.Combine(Path.GetTempPath(), $"serial-scout-export-{Guid.NewGuid():N}.json");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SessionExportService.WriteAsync(preview, path));
            Assert.False(File.Exists(path));

            await SessionExportService.WriteAsync(preview.Confirm(), path);
            Assert.Equal(preview.Content, await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PlaintextPreviewUsesStableUtcRendererAndLeavesOriginalPayloadsUnchanged()
    {
        var original = Events[0].Payload.ToArray();

        var preview = SessionExportService.CreatePreview(Session, Events, RedactionPreset.ShareSafe, SessionExportFormat.PlainText);

        Assert.Equal("12:00:01.000 RX ip=[REDACTED:IP]\n12:00:02.000 TX status", preview.Content);
        Assert.Equal(original, Events[0].Payload);
    }

    [Fact]
    public void ShareSafeJsonRedactsSensitiveSessionMetadata()
    {
        var session = Session with
        {
            PortPath = "/Users/alice/devices/tty-secret",
            Notes = "Bearer abc.def.ghi from 10.2.3.4",
        };

        var preview = SessionExportService.CreatePreview(session, Events, RedactionPreset.ShareSafe, SessionExportFormat.Json);

        Assert.DoesNotContain("/Users/alice", preview.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", preview.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("10.2.3.4", preview.Content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:PATH]", preview.Content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:TOKEN]", preview.Content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:IP]", preview.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingExportDestinationIsRejectedWithoutChangingItsBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"serial-scout-existing-{Guid.NewGuid():N}.json");
        var original = "existing content"u8.ToArray();
        await File.WriteAllBytesAsync(path, original);
        try
        {
            var preview = SessionExportService.CreatePreview(Session, Events, RedactionPreset.ShareSafe, SessionExportFormat.Json);

            await Assert.ThrowsAsync<IOException>(() => SessionExportService.WriteAsync(preview.Confirm(), path));

            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
