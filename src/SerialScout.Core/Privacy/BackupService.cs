using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Storage;

namespace SerialScout.Core.Privacy;

/// <summary>Exact local backup preview requiring acknowledgement of sensitive content.</summary>
/// <param name="Content">Versioned JSON to review and write.</param>
/// <param name="ContainsSensitiveData">Always true because profiles and raw logs may contain secrets.</param>
/// <param name="IsConfirmed">Whether the user acknowledged the warning for this preview.</param>
public sealed record BackupPreview(string Content, bool ContainsSensitiveData = true, bool IsConfirmed = false)
{
    /// <summary>Returns a copy authorized for local file output.</summary>
    public BackupPreview Confirm() => this with { IsConfirmed = true };
}

/// <summary>Counts and conflict information returned after a non-destructive restore.</summary>
/// <param name="ProfilesImported">Profiles inserted with new local IDs.</param>
/// <param name="ProfilesSkipped">Profiles skipped because an existing name was preserved.</param>
/// <param name="SessionsImported">Sessions inserted with new local IDs.</param>
/// <param name="Warnings">Human-readable conflict details.</param>
public sealed record BackupRestoreResult(
    int ProfilesImported,
    int ProfilesSkipped,
    int SessionsImported,
    IReadOnlyList<string> Warnings);

/// <summary>Creates and restores bounded, versioned, local-only profile/session backups.</summary>
public static class BackupService
{
    /// <summary>Current backup document version.</summary>
    public const int FormatVersion = 1;

    /// <summary>Warning that must be acknowledged before backup or restore.</summary>
    public const string SensitiveDataWarning =
        "Backups contain raw session traffic, device identifiers, paths, and profile notes. Store and share them as sensitive files.";

    private const int MaxDocumentCharacters = 20 * 1024 * 1024;
    private const long MaxDocumentBytes = 20L * 1024 * 1024;
    private const int MaxProfiles = 10_000;
    private const int MaxSessions = 100_000;
    private const int MaxEventsPerSession = 100_000;
    private const int MaxEventBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    /// <summary>Reads an untrusted backup without allocating beyond the document limit.</summary>
    public static async Task<string> ReadBoundedAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (file.Length == 0 || file.Length > MaxDocumentBytes)
        {
            throw new InvalidDataException("Backup is empty or exceeds the 20 MiB safety limit.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
        var content = new StringBuilder((int)Math.Min(file.Length, MaxDocumentCharacters));
        var buffer = new char[64 * 1024];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (content.Length > MaxDocumentCharacters - read)
                {
                    throw new InvalidDataException("Backup is empty or exceeds the 20 MiB safety limit.");
                }

                content.Append(buffer, 0, read);
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Backup is not valid UTF-8 text.", ex);
        }

        return content.ToString();
    }

    /// <summary>Builds deterministic JSON from profiles and sessions in their store ordering.</summary>
    public static BackupPreview CreatePreview(ProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var snapshot = store.CreateSnapshot();
        ValidateSnapshotLimits(snapshot);
        var document = new BackupDocument
        {
            FormatVersion = FormatVersion,
            Profiles = snapshot.Profiles.Select(profile => (BackupProfile?)ToBackupProfile(profile)).ToList(),
            Sessions = snapshot.Sessions
                .OrderBy(item => item.Metadata.StartedUtc)
                .ThenBy(item => item.Metadata.Id)
                .Select(item => (BackupSession?)ToBackupSession(item))
                .ToList(),
        };
        var content = JsonSerializer.Serialize(document, JsonOptions);
        if (content.Length > MaxDocumentCharacters || Encoding.UTF8.GetByteCount(content) > MaxDocumentBytes)
        {
            throw new InvalidDataException("Generated backup exceeds the 20 MiB safety limit and cannot be restored safely.");
        }

        _ = ParseAndValidate(content);
        return new BackupPreview(content);
    }

    /// <summary>Writes only an explicitly confirmed sensitive backup preview.</summary>
    public static Task WriteAsync(BackupPreview preview, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!preview.IsConfirmed)
        {
            throw new InvalidOperationException(SensitiveDataWarning);
        }

        return AtomicFileWriter.WriteNewTextAsync(path, preview.Content, cancellationToken);
    }

    /// <summary>
    /// Validates an untrusted document in full, then imports new rows. Existing profiles
    /// are never updated; exact name conflicts are skipped and reported.
    /// </summary>
    public static Task<BackupRestoreResult> RestoreAsync(
        ProfileStore store,
        string content,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed)
        {
            throw new InvalidOperationException(SensitiveDataWarning);
        }

        var validated = ParseAndValidate(content);
        var result = store.ExecuteInTransaction(() =>
        {
            var existingNames = store.ListProfiles().Select(profile => profile.Name.Trim()).ToHashSet(StringComparer.Ordinal);
            var profileIdMap = new Dictionary<long, long>();
            var warnings = new List<string>();
            var importedProfiles = 0;
            var skippedProfiles = 0;
            foreach (var item in validated.Profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!existingNames.Add(item.Profile.Name))
                {
                    skippedProfiles++;
                    warnings.Add($"Profile '{item.Profile.Name}' already exists and was not overwritten.");
                    continue;
                }

                var created = store.ImportProfileAsNew(item.Profile);
                profileIdMap.Add(item.OriginalId, created.Id);
                importedProfiles++;
            }

            var importedSessions = 0;
            foreach (var item in validated.Sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long? profileId = null;
                if (item.ProfileOriginalId is long originalProfileId)
                {
                    if (profileIdMap.TryGetValue(originalProfileId, out var mappedProfileId))
                    {
                        profileId = mappedProfileId;
                    }
                    else
                    {
                        warnings.Add($"Session {item.OriginalId} was restored unbound because its profile was not imported.");
                    }
                }

                var created = store.CreateSession(item.Metadata.PortPath, item.Metadata.StartedUtc, profileId, item.Metadata.Notes);
                foreach (var logEvent in item.Events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    store.AppendSessionEvent(created.Id, logEvent);
                }

                if (item.Metadata.EndedUtc is DateTimeOffset endedUtc)
                {
                    store.EndSession(created.Id, endedUtc);
                }

                importedSessions++;
            }

            return new BackupRestoreResult(importedProfiles, skippedProfiles, importedSessions, warnings);
        });
        return Task.FromResult(result);
    }

    private static ValidatedBackup ParseAndValidate(string content)
    {
        if (content.Length == 0 || content.Length > MaxDocumentCharacters)
        {
            throw new InvalidDataException("Backup is empty or exceeds the 20 MiB safety limit.");
        }

        BackupDocument document;
        try
        {
            document = JsonSerializer.Deserialize<BackupDocument>(content, JsonOptions)
                ?? throw new InvalidDataException("Backup JSON has no document root.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Backup JSON is malformed or contains unknown fields.", ex);
        }

        if (document.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported backup format version {document.FormatVersion}.");
        }

        if (document.Profiles is null || document.Sessions is null ||
            document.Profiles.Count > MaxProfiles || document.Sessions.Count > MaxSessions)
        {
            throw new InvalidDataException("Backup collections are missing or exceed safety limits.");
        }

        var profileIds = new HashSet<long>();
        var profileNames = new HashSet<string>(StringComparer.Ordinal);
        var profiles = new List<ValidatedProfile>(document.Profiles.Count);
        try
        {
            foreach (var item in document.Profiles)
            {
                if (item is null || item.OriginalId <= 0 || !profileIds.Add(item.OriginalId) ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256 ||
                    !profileNames.Add(item.Name.Trim()) || item.Notes?.Length > 100_000 ||
                    !Enum.IsDefined((LineParity)item.Parity) || !Enum.IsDefined((LineStopBits)item.StopBits))
                {
                    throw new InvalidDataException("Backup contains an invalid or duplicate profile.");
                }

                profiles.Add(new ValidatedProfile(item.OriginalId, new DeviceProfile
                {
                    Name = item.Name.Trim(),
                    Rule = new ProfileMatchRule(item.VendorId, item.ProductId, item.SerialFingerprint, item.ProductHint, item.ManufacturerHint),
                    LineSettings = new LineSettings(item.BaudRate, item.DataBits, (LineParity)item.Parity, (LineStopBits)item.StopBits),
                    Notes = item.Notes,
                    CreatedUtc = item.CreatedUtc,
                    LastSeenUtc = item.LastSeenUtc,
                }));
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Backup contains out-of-range profile settings.", ex);
        }

        var sessionIds = new HashSet<long>();
        var sessions = new List<ValidatedSession>(document.Sessions.Count);
        foreach (var item in document.Sessions)
        {
            if (item is null || item.OriginalId <= 0 || !sessionIds.Add(item.OriginalId) ||
                item.ProfileOriginalId is long profileOriginalId && !profileIds.Contains(profileOriginalId) ||
                string.IsNullOrWhiteSpace(item.PortPath) || item.PortPath.Length > 4096 ||
                item.Notes?.Length > 100_000 || item.EndedUtc < item.StartedUtc ||
                item.Events is null || item.Events.Count > MaxEventsPerSession)
            {
                throw new InvalidDataException("Backup contains an invalid session.");
            }

            var events = new List<LogEvent>(item.Events.Count);
            foreach (var eventItem in item.Events)
            {
                if (eventItem is null)
                {
                    throw new InvalidDataException("Backup contains a null event.");
                }

                byte[] payload;
                try
                {
                    payload = Convert.FromBase64String(eventItem.PayloadBase64 ?? string.Empty);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("Backup contains malformed event data.", ex);
                }

                if (payload.Length == 0 || payload.Length > MaxEventBytes || !Enum.IsDefined((LogEventDirection)eventItem.Direction))
                {
                    throw new InvalidDataException("Backup contains an invalid event.");
                }

                events.Add(new LogEvent(eventItem.Utc, (LogEventDirection)eventItem.Direction, payload));
            }

            sessions.Add(new ValidatedSession(
                item.OriginalId,
                item.ProfileOriginalId,
                new SessionMetadata
                {
                    PortPath = item.PortPath,
                    StartedUtc = item.StartedUtc,
                    EndedUtc = item.EndedUtc,
                    Notes = item.Notes,
                },
                events));
        }

        return new ValidatedBackup(profiles, sessions);
    }

    private static void ValidateSnapshotLimits(ProfileStoreSnapshot snapshot)
    {
        if (snapshot.Profiles.Count > MaxProfiles || snapshot.Sessions.Count > MaxSessions)
        {
            throw new InvalidDataException("Generated backup exceeds profile or session safety limits.");
        }

        foreach (var session in snapshot.Sessions)
        {
            if (session.Events.Count > MaxEventsPerSession ||
                session.Events.Any(logEvent => logEvent.Payload.Length == 0 || logEvent.Payload.Length > MaxEventBytes))
            {
                throw new InvalidDataException("Generated backup contains event data that exceeds the restore safety limit.");
            }
        }
    }

    private static BackupProfile ToBackupProfile(DeviceProfile profile) => new()
    {
        OriginalId = profile.Id,
        Name = profile.Name,
        VendorId = profile.Rule.VendorId,
        ProductId = profile.Rule.ProductId,
        SerialFingerprint = profile.Rule.SerialFingerprint,
        ProductHint = profile.Rule.ProductHint,
        ManufacturerHint = profile.Rule.ManufacturerHint,
        BaudRate = profile.LineSettings.BaudRate,
        DataBits = profile.LineSettings.DataBits,
        Parity = (int)profile.LineSettings.Parity,
        StopBits = (int)profile.LineSettings.StopBits,
        Notes = profile.Notes,
        CreatedUtc = profile.CreatedUtc,
        LastSeenUtc = profile.LastSeenUtc,
    };

    private static BackupSession ToBackupSession(StoredSessionSnapshot item) => new()
    {
        OriginalId = item.Metadata.Id,
        ProfileOriginalId = item.Metadata.ProfileId,
        PortPath = item.Metadata.PortPath,
        StartedUtc = item.Metadata.StartedUtc,
        EndedUtc = item.Metadata.EndedUtc,
        Notes = item.Metadata.Notes,
        Events = item.Events.Select(logEvent => (BackupEvent?)new BackupEvent
        {
            Utc = logEvent.Utc,
            Direction = (int)logEvent.Direction,
            PayloadBase64 = Convert.ToBase64String(logEvent.Payload),
        }).ToList(),
    };

    private sealed class BackupDocument
    {
        public int FormatVersion { get; set; }

        public List<BackupProfile?>? Profiles { get; set; }

        public List<BackupSession?>? Sessions { get; set; }
    }

    private sealed class BackupProfile
    {
        public long OriginalId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int VendorId { get; set; }

        public int ProductId { get; set; }

        public string? SerialFingerprint { get; set; }

        public string? ProductHint { get; set; }

        public string? ManufacturerHint { get; set; }

        public int BaudRate { get; set; }

        public int DataBits { get; set; }

        public int Parity { get; set; }

        public int StopBits { get; set; }

        public string? Notes { get; set; }

        public DateTimeOffset? CreatedUtc { get; set; }

        public DateTimeOffset? LastSeenUtc { get; set; }
    }

    private sealed class BackupSession
    {
        public long OriginalId { get; set; }

        public long? ProfileOriginalId { get; set; }

        public string PortPath { get; set; } = string.Empty;

        public DateTimeOffset StartedUtc { get; set; }

        public DateTimeOffset? EndedUtc { get; set; }

        public string? Notes { get; set; }

        public List<BackupEvent?>? Events { get; set; }
    }

    private sealed class BackupEvent
    {
        public DateTimeOffset Utc { get; set; }

        public int Direction { get; set; }

        public string? PayloadBase64 { get; set; }
    }

    private sealed record ValidatedProfile(long OriginalId, DeviceProfile Profile);

    private sealed record ValidatedSession(
        long OriginalId,
        long? ProfileOriginalId,
        SessionMetadata Metadata,
        IReadOnlyList<LogEvent> Events);

    private sealed record ValidatedBackup(
        IReadOnlyList<ValidatedProfile> Profiles,
        IReadOnlyList<ValidatedSession> Sessions);
}
