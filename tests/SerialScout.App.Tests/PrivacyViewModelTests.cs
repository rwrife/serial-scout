using SerialScout.App.ViewModels;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Storage;

namespace SerialScout.App.Tests;

public sealed class PrivacyViewModelTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SessionExportFlowRequiresPreviewThenConfirmationAndInvalidatesOnPresetChange()
    {
        using var store = new ProfileStore(":memory:");
        var session = store.CreateSession("COM8", Started);
        store.AppendSessionEvent(session.Id, new LogEvent(Started, LogEventDirection.Received, "host=10.2.3.4"u8.ToArray()));
        var errors = new List<string>();
        var vm = new PrivacyViewModel(store, errors.Add);
        vm.SelectedSession = Assert.Single(vm.Sessions);
        vm.RedactionPresetIndex = 4;

        vm.CreateExportPreview();

        Assert.Contains("[REDACTED:IP]", vm.ExportPreviewText, StringComparison.Ordinal);
        Assert.False(vm.CanWriteExport);
        vm.IsExportConfirmed = true;
        Assert.True(vm.CanWriteExport);

        var path = Path.Combine(Path.GetTempPath(), $"privacy-vm-{Guid.NewGuid():N}.txt");
        try
        {
            await vm.WriteExportAsync(path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }

        vm.RedactionPresetIndex = 0;
        Assert.False(vm.CanWriteExport);
        Assert.Equal(string.Empty, vm.ExportPreviewText);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task BackupRestoreAndRetentionExposeWarningsAndConfirmations()
    {
        using var store = new ProfileStore(":memory:");
        store.CreateProfile(new DeviceProfile { Name = "board", Rule = new ProfileMatchRule(1, 2) });
        store.CreateSession("COM1", Started);
        store.CreateSession("COM2", Started.AddMinutes(1));
        var vm = new PrivacyViewModel(store, _ => { });

        vm.CreateBackupPreview();
        Assert.Contains("sensitive", vm.BackupWarningText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CanWriteBackup);
        vm.IsSensitiveBackupConfirmed = true;
        Assert.True(vm.CanWriteBackup);

        vm.RetentionCountIndex = 0; // keep newest one
        vm.IsRetentionConfirmed = true;
        vm.ApplyRetention();
        Assert.Single(store.ListSessions());

        var result = await vm.RestoreAsync(vm.BackupPreviewText);
        Assert.Contains("restored", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not overwritten", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackupPreviewLimitFailureIsReportedWithoutEscapingTheUiViewModel()
    {
        using var store = new ProfileStore(":memory:");
        var session = store.CreateSession("COM1", Started);
        store.AppendSessionEvent(session.Id, new LogEvent(Started, LogEventDirection.Received, new byte[(1024 * 1024) + 1]));
        var errors = new List<string>();
        var vm = new PrivacyViewModel(store, errors.Add);

        var exception = Record.Exception(vm.CreateBackupPreview);

        Assert.Null(exception);
        Assert.Contains("limit", Assert.Single(errors), StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.CanWriteBackup);
    }

    [Fact]
    public async Task DatabaseSidecarDestinationIsRejectedEvenWhenItDoesNotExist()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"privacy-store-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var store = new ProfileStore(databasePath);
            var session = store.CreateSession("COM1", Started);
            store.AppendSessionEvent(session.Id, new LogEvent(Started, LogEventDirection.Received, "ok"u8.ToArray()));
            var vm = new PrivacyViewModel(store, _ => { }) { SelectedSession = null };
            vm.SelectedSession = Assert.Single(vm.Sessions);
            vm.CreateExportPreview();
            vm.IsExportConfirmed = true;
            var sidecarPath = databasePath + "-wal";

            await Assert.ThrowsAsync<IOException>(() => vm.WriteExportAsync(sidecarPath));

            Assert.False(File.Exists(sidecarPath));
        }
        finally
        {
            // Disposal returns SQLite's handle to its pool; Windows cannot delete it yet.
            using var pooledConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                }.ToString());
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pooledConnection);
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    [Fact]
    public void RetentionReceivesAndProtectsTheActiveTerminalSessionId()
    {
        using var store = new ProfileStore(":memory:");
        var active = store.CreateSession("COM-active", Started);
        var newer = store.CreateSession("COM-newer", Started.AddMinutes(1));
        store.EndSession(newer.Id, Started.AddMinutes(2));
        var vm = new PrivacyViewModel(store, _ => { }, () => active.Id)
        {
            RetentionCountIndex = 0,
            IsRetentionConfirmed = true,
        };

        vm.ApplyRetention();

        Assert.Equal(2, store.ListSessions().Count);
        Assert.Contains("active terminal session was protected", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}
