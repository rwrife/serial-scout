using System.Globalization;
using Microsoft.Data.Sqlite;
using SerialScout.Core.Profiles;

namespace SerialScout.Core.Storage;

/// <summary>
/// Local-first SQLite persistence for <see cref="DeviceProfile"/>s and
/// <see cref="SessionMetadata"/> rows. Everything is stored in one file on the user's
/// machine — no network, no account, no telemetry. The schema is versioned through
/// <c>PRAGMA user_version</c> so future issues can extend it without guessing.
/// </summary>
public sealed class ProfileStore : IDisposable
{
    /// <summary>Current schema version written into <c>PRAGMA user_version</c>.</summary>
    public const int SchemaVersion = 1;

    private const string TimestampFormat = "O";

    private readonly SqliteConnection _connection;

    /// <summary>
    /// Coarse lock guarding the single shared connection: the desktop UI reads and
    /// writes the store from background tasks (save/scan) while the UI thread reads
    /// history, and one <see cref="SqliteConnection"/> is not thread-safe.
    /// </summary>
    private readonly object _gate = new();

    private bool _disposed;

    /// <summary>
    /// Opens (creating if needed) the store at <paramref name="databasePath"/> and applies
    /// the schema. A path of <c>:memory:</c> yields an ephemeral store for tests.
    /// </summary>
    /// <param name="databasePath">Filesystem path to the SQLite database, or <c>:memory:</c>.</param>
    public ProfileStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    /// <summary>Total number of stored profiles.</summary>
    public int CountProfiles()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM profile;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Inserts a new profile, assigning id, created time, and unique-name enforcement.
    /// </summary>
    /// <param name="profile">Profile to create; <see cref="DeviceProfile.Id"/> and <see cref="DeviceProfile.CreatedUtc"/> are ignored.</param>
    /// <returns>The stored profile with id and created time populated.</returns>
    public DeviceProfile CreateProfile(DeviceProfile profile)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                throw new ArgumentException("Profiles require a non-blank name.", nameof(profile));
            }

            var createdUtc = DateTimeOffset.UtcNow;
            using (var insert = _connection.CreateCommand())
            {
                insert.CommandText = """
                INSERT INTO profile (name, vendor_id, product_id, serial_fingerprint, product_hint,
                                     manufacturer_hint, baud_rate, data_bits, parity, stop_bits,
                                     notes, created_utc, last_seen_utc)
                VALUES ($name, $vendor, $product, $serial, $productHint,
                        $manufacturerHint, $baud, $dataBits, $parity, $stopBits,
                        $notes, $created, $lastSeen);
                """;
                insert.Parameters.AddWithValue("$name", profile.Name.Trim());
                insert.Parameters.AddWithValue("$vendor", profile.Rule.VendorId);
                insert.Parameters.AddWithValue("$product", profile.Rule.ProductId);
                insert.Parameters.AddWithValue("$serial", (object?)profile.Rule.SerialFingerprint ?? DBNull.Value);
                insert.Parameters.AddWithValue("$productHint", (object?)profile.Rule.ProductHint ?? DBNull.Value);
                insert.Parameters.AddWithValue("$manufacturerHint", (object?)profile.Rule.ManufacturerHint ?? DBNull.Value);
                insert.Parameters.AddWithValue("$baud", profile.LineSettings.BaudRate);
                insert.Parameters.AddWithValue("$dataBits", profile.LineSettings.DataBits);
                insert.Parameters.AddWithValue("$parity", (int)profile.LineSettings.Parity);
                insert.Parameters.AddWithValue("$stopBits", (int)profile.LineSettings.StopBits);
                insert.Parameters.AddWithValue("$notes", (object?)profile.Notes ?? DBNull.Value);
                insert.Parameters.AddWithValue("$created", createdUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue(
                    "$lastSeen",
                    (object?)(profile.LastSeenUtc?.ToString(TimestampFormat, CultureInfo.InvariantCulture)) ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }

            var id = Convert.ToInt64(LastRowId(), CultureInfo.InvariantCulture);
            return profile with { Id = id, CreatedUtc = createdUtc };
        }
    }

    /// <summary>Loads one profile by id, or <see langword="null"/> when absent.</summary>
    /// <param name="id">Profile id.</param>
    public DeviceProfile? GetProfile(long id)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT " + ProfileColumns + " FROM profile WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadProfile(reader) : null;
        }
    }

    /// <summary>Lists all profiles ordered by id ascending for deterministic output.</summary>
    public IReadOnlyList<DeviceProfile> ListProfiles()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT " + ProfileColumns + " FROM profile ORDER BY id;";
            using var reader = command.ExecuteReader();
            var results = new List<DeviceProfile>();
            while (reader.Read())
            {
                results.Add(ReadProfile(reader));
            }

            return results;
        }
    }

    /// <summary>
    /// Updates name, rule, line settings, and notes of an existing profile.
    /// </summary>
    /// <param name="profile">Profile with updated fields; <see cref="DeviceProfile.Id"/> selects the row.</param>
    /// <returns><see langword="true"/> when a row was updated.</returns>
    public bool UpdateProfile(DeviceProfile profile)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                throw new ArgumentException("Profiles require a non-blank name.", nameof(profile));
            }

            using var command = _connection.CreateCommand();
            command.CommandText = """
            UPDATE profile
               SET name = $name, vendor_id = $vendor, product_id = $product,
                   serial_fingerprint = $serial, product_hint = $productHint,
                   manufacturer_hint = $manufacturerHint, baud_rate = $baud,
                   data_bits = $dataBits, parity = $parity, stop_bits = $stopBits,
                   notes = $notes
             WHERE id = $id;
            """;
            command.Parameters.AddWithValue("$name", profile.Name.Trim());
            command.Parameters.AddWithValue("$vendor", profile.Rule.VendorId);
            command.Parameters.AddWithValue("$product", profile.Rule.ProductId);
            command.Parameters.AddWithValue("$serial", (object?)profile.Rule.SerialFingerprint ?? DBNull.Value);
            command.Parameters.AddWithValue("$productHint", (object?)profile.Rule.ProductHint ?? DBNull.Value);
            command.Parameters.AddWithValue("$manufacturerHint", (object?)profile.Rule.ManufacturerHint ?? DBNull.Value);
            command.Parameters.AddWithValue("$baud", profile.LineSettings.BaudRate);
            command.Parameters.AddWithValue("$dataBits", profile.LineSettings.DataBits);
            command.Parameters.AddWithValue("$parity", (int)profile.LineSettings.Parity);
            command.Parameters.AddWithValue("$stopBits", (int)profile.LineSettings.StopBits);
            command.Parameters.AddWithValue("$notes", (object?)profile.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", profile.Id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Deletes a profile. Existing session rows survive with a <see langword="null"/>
    /// profile binding so history is not silently destroyed.
    /// </summary>
    /// <param name="id">Profile id.</param>
    /// <returns><see langword="true"/> when a row was deleted.</returns>
    public bool DeleteProfile(long id)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using (var clearBindings = _connection.CreateCommand())
            {
                clearBindings.CommandText = "UPDATE session_metadata SET profile_id = NULL WHERE profile_id = $id;";
                clearBindings.Parameters.AddWithValue("$id", id);
                clearBindings.ExecuteNonQuery();
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM profile WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Stamps a profile's last-seen time, which drives staleness demotion in
    /// <see cref="ProfileMatcher"/>.
    /// </summary>
    /// <param name="id">Profile id.</param>
    /// <param name="lastSeenUtc">UTC time the device was positively identified.</param>
    /// <returns><see langword="true"/> when a row was updated.</returns>
    public bool TouchProfile(long id, DateTimeOffset lastSeenUtc)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE profile SET last_seen_utc = $lastSeen WHERE id = $id;";
            command.Parameters.AddWithValue("$lastSeen", lastSeenUtc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Records a new session row and returns it with its assigned id.
    /// </summary>
    /// <param name="portPath">OS port path used.</param>
    /// <param name="startedUtc">UTC session start time.</param>
    /// <param name="profileId">Bound profile id, or <see langword="null"/> for unbound sessions.</param>
    /// <param name="notes">Optional notes.</param>
    public SessionMetadata CreateSession(string portPath, DateTimeOffset startedUtc, long? profileId = null, string? notes = null)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(portPath);

            using (var insert = _connection.CreateCommand())
            {
                insert.CommandText = """
                INSERT INTO session_metadata (profile_id, port_path, started_utc, ended_utc, notes)
                VALUES ($profileId, $portPath, $started, NULL, $notes);
                """;
                insert.Parameters.AddWithValue("$profileId", (object?)profileId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$portPath", portPath.Trim());
                insert.Parameters.AddWithValue("$started", startedUtc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }

            var id = Convert.ToInt64(LastRowId(), CultureInfo.InvariantCulture);
            return new SessionMetadata
            {
                Id = id,
                ProfileId = profileId,
                PortPath = portPath.Trim(),
                StartedUtc = startedUtc.ToUniversalTime(),
                Notes = notes,
            };
        }
    }

    /// <summary>Marks a session as ended at <paramref name="endedUtc"/>.</summary>
    /// <param name="id">Session row id.</param>
    /// <param name="endedUtc">UTC end time.</param>
    /// <returns><see langword="true"/> when a row was updated.</returns>
    public bool EndSession(long id, DateTimeOffset endedUtc)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE session_metadata SET ended_utc = $ended WHERE id = $id;";
            command.Parameters.AddWithValue("$ended", endedUtc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Lists session rows, newest start first.</summary>
    public IReadOnlyList<SessionMetadata> ListSessions()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
            SELECT id, profile_id, port_path, started_utc, ended_utc, notes
              FROM session_metadata
             ORDER BY started_utc DESC, id DESC;
            """;
            using var reader = command.ExecuteReader();
            var results = new List<SessionMetadata>();
            while (reader.Read())
            {
                results.Add(new SessionMetadata
                {
                    Id = reader.GetInt64(0),
                    ProfileId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    PortPath = reader.GetString(2),
                    StartedUtc = ParseTimestamp(reader.GetString(3)),
                    EndedUtc = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                    Notes = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }

            return results;
        }
    }

    /// <summary>Disposes the underlying SQLite connection.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _connection.Dispose();
        _disposed = true;
    }

    private const string ProfileColumns =
        "id, name, vendor_id, product_id, serial_fingerprint, product_hint, manufacturer_hint, "
        + "baud_rate, data_bits, parity, stop_bits, notes, created_utc, last_seen_utc";

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version;
            """;
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
        {
            return;
        }

        using var migrate = _connection.CreateCommand();
        migrate.CommandText = """
            BEGIN;
            CREATE TABLE IF NOT EXISTS profile (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                name              TEXT NOT NULL UNIQUE,
                vendor_id         INTEGER NOT NULL,
                product_id        INTEGER NOT NULL,
                serial_fingerprint TEXT,
                product_hint      TEXT,
                manufacturer_hint TEXT,
                baud_rate         INTEGER NOT NULL,
                data_bits         INTEGER NOT NULL,
                parity            INTEGER NOT NULL,
                stop_bits         INTEGER NOT NULL,
                notes             TEXT,
                created_utc       TEXT NOT NULL,
                last_seen_utc     TEXT
            );
            CREATE TABLE IF NOT EXISTS session_metadata (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                profile_id INTEGER REFERENCES profile(id) ON DELETE SET NULL,
                port_path  TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                ended_utc  TEXT,
                notes      TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_session_profile ON session_metadata(profile_id);
            PRAGMA user_version = {user_version};
            COMMIT;
            """;
        migrate.CommandText = migrate.CommandText.Replace(
            "{user_version}",
            SchemaVersion.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        migrate.ExecuteNonQuery();
    }

    private string LastRowId()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }

    private static DeviceProfile ReadProfile(SqliteDataReader reader)
    {
        return new DeviceProfile
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            Rule = new ProfileMatchRule(
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)),
            LineSettings = new LineSettings(
                reader.GetInt32(7),
                reader.GetInt32(8),
                (LineParity)reader.GetInt32(9),
                (LineStopBits)reader.GetInt32(10)),
            Notes = reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedUtc = ParseTimestamp(reader.GetString(12)),
            LastSeenUtc = reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)),
        };
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
