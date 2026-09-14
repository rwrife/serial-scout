using SerialScout.Core.Privacy;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Storage;
using Microsoft.Data.Sqlite;

namespace SerialScout.Core.Tests;

public sealed class BackupServiceTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VersionedBackupRequiresConfirmationAndRestoreNeverOverwritesNameConflicts()
    {
        using var source = new ProfileStore(":memory:");
        var profile = source.CreateProfile(new DeviceProfile
        {
            Name = "Board",
            Rule = new ProfileMatchRule(0x1234, 0x5678, "SERIAL"),
            Notes = "source notes",
        });
        var session = source.CreateSession("/dev/ttyUSB0", Started, profile.Id, "captured");
        source.AppendSessionEvent(session.Id, new LogEvent(Started.AddSeconds(1), LogEventDirection.Received, "hello"u8.ToArray()));
        source.EndSession(session.Id, Started.AddMinutes(1));

        var preview = BackupService.CreatePreview(source);
        Assert.True(preview.ContainsSensitiveData);
        Assert.Contains("\"formatVersion\": 1", preview.Content, StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), $"serial-scout-backup-{Guid.NewGuid():N}.json");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => BackupService.WriteAsync(preview, path));
            await BackupService.WriteAsync(preview.Confirm(), path);

            using var target = new ProfileStore(":memory:");
            target.CreateProfile(new DeviceProfile
            {
                Name = "Board",
                Rule = new ProfileMatchRule(1, 2),
                Notes = "keep me",
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => BackupService.RestoreAsync(target, preview.Content, confirmed: false));
            var result = await BackupService.RestoreAsync(target, preview.Content, confirmed: true);

            Assert.Equal(0, result.ProfilesImported);
            Assert.Equal(1, result.ProfilesSkipped);
            Assert.Equal(1, result.SessionsImported);
            Assert.Equal("keep me", Assert.Single(target.ListProfiles()).Notes);
            var restoredSession = Assert.Single(target.ListSessions());
            Assert.Null(restoredSession.ProfileId);
            Assert.Equal("hello"u8.ToArray(), Assert.Single(target.ListSessionEvents(restoredSession.Id)).Payload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidOrUnsupportedBackupIsRejectedBeforeStoreMutation()
    {
        using var target = new ProfileStore(":memory:");
        target.CreateProfile(new DeviceProfile { Name = "existing", Rule = new ProfileMatchRule(1, 2) });

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.RestoreAsync(
            target,
            "{\"formatVersion\":999,\"profiles\":[],\"sessions\":[]}",
            confirmed: true));

        Assert.Single(target.ListProfiles());
        Assert.Empty(target.ListSessions());
    }

    [Theory]
    [InlineData("profiles", "{\"formatVersion\":1,\"profiles\":[null],\"sessions\":[]}")]
    [InlineData("sessions", "{\"formatVersion\":1,\"profiles\":[],\"sessions\":[null]}")]
    [InlineData("events", "{\"formatVersion\":1,\"profiles\":[],\"sessions\":[{\"originalId\":1,\"portPath\":\"COM1\",\"startedUtc\":\"2026-09-14T09:00:00Z\",\"events\":[null]}]}")]
    public async Task NullArrayEntriesAreRejectedAsInvalidData(string _, string json)
    {
        using var target = new ProfileStore(":memory:");

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.RestoreAsync(target, json, confirmed: true));

        Assert.Empty(target.ListProfiles());
        Assert.Empty(target.ListSessions());
    }

    [Fact]
    public async Task WhitespaceNormalizedProfileConflictIsSkipped()
    {
        using var source = new ProfileStore(":memory:");
        source.CreateProfile(new DeviceProfile { Name = "Board", Rule = new ProfileMatchRule(1, 2) });
        var json = BackupService.CreatePreview(source).Content.Replace("\"Board\"", "\" Board \"", StringComparison.Ordinal);
        using var target = new ProfileStore(":memory:");
        target.CreateProfile(new DeviceProfile { Name = "Board", Rule = new ProfileMatchRule(3, 4) });

        var result = await BackupService.RestoreAsync(target, json, confirmed: true);

        Assert.Equal(1, result.ProfilesSkipped);
        Assert.Single(target.ListProfiles());
    }

    [Fact]
    public async Task FailedRestoreRollsBackProfilesSessionsAndEvents()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"serial-scout-rollback-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var source = new ProfileStore(":memory:");
            var profile = source.CreateProfile(new DeviceProfile { Name = "imported", Rule = new ProfileMatchRule(1, 2) });
            var session = source.CreateSession("COM2", Started, profile.Id);
            source.AppendSessionEvent(session.Id, new LogEvent(Started, LogEventDirection.Received, "payload"u8.ToArray()));
            var backup = BackupService.CreatePreview(source).Content;

            using var target = new ProfileStore(databasePath);
            target.CreateProfile(new DeviceProfile { Name = "existing", Rule = new ProfileMatchRule(3, 4) });
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_restore BEFORE INSERT ON session_event BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
                command.ExecuteNonQuery();
            }

            await Assert.ThrowsAsync<SqliteException>(() => BackupService.RestoreAsync(target, backup, confirmed: true));

            Assert.Collection(target.ListProfiles(), item => Assert.Equal("existing", item.Name));
            Assert.Empty(target.ListSessions());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public void GeneratedBackupRejectsEventsThatRestoreWouldReject()
    {
        using var store = new ProfileStore(":memory:");
        var session = store.CreateSession("COM1", Started);
        store.AppendSessionEvent(session.Id, new LogEvent(Started, LogEventDirection.Received, new byte[(1024 * 1024) + 1]));

        var error = Assert.Throws<InvalidDataException>(() => BackupService.CreatePreview(store));

        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingBackupDestinationIsRejectedWithoutChangingItsBytes()
    {
        using var store = new ProfileStore(":memory:");
        var preview = BackupService.CreatePreview(store).Confirm();
        var path = Path.Combine(Path.GetTempPath(), $"serial-scout-existing-{Guid.NewGuid():N}.json");
        var original = "database bytes"u8.ToArray();
        await File.WriteAllBytesAsync(path, original);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => BackupService.WriteAsync(preview, path));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BoundedReaderRejectsOversizedFileBeforeReadingItAll()
    {
        var path = Path.Combine(Path.GetTempPath(), $"serial-scout-oversized-{Guid.NewGuid():N}.json");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength((20L * 1024 * 1024) + 1);
        }

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.ReadBoundedAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
