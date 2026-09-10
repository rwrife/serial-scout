using Microsoft.Data.Sqlite;
using SerialScout.Core.Profiles;
using SerialScout.Core.Storage;

namespace SerialScout.Core.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 9, 10, 8, 30, 0, TimeSpan.Zero);

    private readonly ProfileStore _store = new(":memory:");

    [Fact]
    public void CreateThenGetRoundTripsEveryField()
    {
        var created = _store.CreateProfile(new DeviceProfile
        {
            Name = "ESP32 dev",
            Rule = new ProfileMatchRule(0x1A86, 0x7523, serialFingerprint: "ABC123", productHint: "USB Serial"),
            LineSettings = new LineSettings(9600, 7, LineParity.Even, LineStopBits.Two),
            Notes = "bench board",
        });

        var loaded = _store.GetProfile(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("ESP32 dev", loaded.Name);
        Assert.Equal(0x1A86, loaded.Rule.VendorId);
        Assert.Equal(0x7523, loaded.Rule.ProductId);
        Assert.Equal("ABC123", loaded.Rule.SerialFingerprint);
        Assert.Equal("USB Serial", loaded.Rule.ProductHint);
        Assert.Null(loaded.Rule.ManufacturerHint);
        Assert.Equal(9600, loaded.LineSettings.BaudRate);
        Assert.Equal(7, loaded.LineSettings.DataBits);
        Assert.Equal(LineParity.Even, loaded.LineSettings.Parity);
        Assert.Equal(LineStopBits.Two, loaded.LineSettings.StopBits);
        Assert.Equal("bench board", loaded.Notes);
        Assert.NotNull(loaded.CreatedUtc);
        Assert.Null(loaded.LastSeenUtc);
    }

    [Fact]
    public void DuplicateNamesAreRejected()
    {
        _store.CreateProfile(NewProfile("dup"));

        var duplicate = NewProfile("dup");
        Assert.Throws<SqliteException>(() => _store.CreateProfile(duplicate));
        Assert.Equal(1, _store.CountProfiles());
    }

    [Fact]
    public void BlankNamesAreRejected()
    {
        var blank = new DeviceProfile { Name = "   ", Rule = new ProfileMatchRule(1, 2) };
        Assert.Throws<ArgumentException>(() => _store.CreateProfile(blank));
    }

    [Fact]
    public void ListProfilesIsOrderedById()
    {
        var first = _store.CreateProfile(NewProfile("first"));
        var second = _store.CreateProfile(NewProfile("second"));

        var names = _store.ListProfiles().Select(p => p.Name).ToList();

        Assert.Equal(new[] { first.Name, second.Name }, names);
    }

    [Fact]
    public void UpdateProfileChangesStoredFields()
    {
        var created = _store.CreateProfile(NewProfile("renamed"));

        var updated = _store.UpdateProfile(created with
        {
            Name = "renamed-v2",
            Rule = new ProfileMatchRule(0x10C4, 0xEA60, manufacturerHint: "Silicon Labs"),
            LineSettings = new LineSettings(57600),
            Notes = "moved to new chip",
        });

        Assert.True(updated);
        var loaded = _store.GetProfile(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal("renamed-v2", loaded.Name);
        Assert.Equal(0x10C4, loaded.Rule.VendorId);
        Assert.Equal("Silicon Labs", loaded.Rule.ManufacturerHint);
        Assert.Equal(57600, loaded.LineSettings.BaudRate);
        Assert.Equal("moved to new chip", loaded.Notes);
    }

    [Fact]
    public void TouchProfileUpdatesLastSeen()
    {
        var created = _store.CreateProfile(NewProfile("touched"));
        var when = Started.AddHours(2);

        Assert.True(_store.TouchProfile(created.Id, when));

        var loaded = _store.GetProfile(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal(when.ToUniversalTime(), loaded.LastSeenUtc!.Value.ToUniversalTime());
    }

    [Fact]
    public void DeletingProfileKeepsSessionHistoryUnbound()
    {
        var profile = _store.CreateProfile(NewProfile("doomed"));
        _store.CreateSession("COM5", Started, profileId: profile.Id);

        Assert.True(_store.DeleteProfile(profile.Id));
        Assert.Null(_store.GetProfile(profile.Id));

        var session = Assert.Single(_store.ListSessions());
        Assert.Null(session.ProfileId);
        Assert.Equal("COM5", session.PortPath);
    }

    [Fact]
    public void SessionLifecycleRoundTripsAndOrdersNewestFirst()
    {
        var older = _store.CreateSession("COM3", Started);
        var newer = _store.CreateSession("COM4", Started.AddHours(1), notes: "firmware flash");

        Assert.True(_store.EndSession(newer.Id, Started.AddHours(2)));

        var sessions = _store.ListSessions();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(newer.Id, sessions[0].Id);
        Assert.Equal("firmware flash", sessions[0].Notes);
        Assert.NotNull(sessions[0].EndedUtc);
        Assert.Equal(older.Id, sessions[1].Id);
        Assert.Null(sessions[1].EndedUtc);
    }

    [Fact]
    public void SchemaVersionIsCurrentAndIdempotent()
    {
        // Opening a second store over the same file must not fail or re-create tables.
        using var file = new TempDatabaseFile();
        using (var first = new ProfileStore(file.Path))
        {
            first.CreateProfile(NewProfile("persisted"));
        }

        using var second = new ProfileStore(file.Path);
        Assert.Single(second.ListProfiles());
    }

    public void Dispose() => _store.Dispose();

    private static DeviceProfile NewProfile(string name) => new()
    {
        Name = name,
        Rule = new ProfileMatchRule(0x1A86, 0x7523),
    };

    private sealed class TempDatabaseFile : IDisposable
    {
        public TempDatabaseFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"serialscout-test-{Guid.NewGuid():N}.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
