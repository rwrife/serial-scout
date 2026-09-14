using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SerialScout.Core.Privacy;
using SerialScout.Core.Profiles;
using SerialScout.Core.Storage;

namespace SerialScout.App.ViewModels;

/// <summary>
/// Local privacy workspace for selected-session export, sensitive backup/restore, and
/// explicit session retention. Every write is gated by a preview confirmation.
/// </summary>
public sealed class PrivacyViewModel : ViewModelBase
{
    private static readonly int[] RetentionCounts = [1, 10, 50, 100];

    private readonly ProfileStore _store;
    private readonly Action<string> _reportError;
    private readonly Func<long?> _activeSessionId;
    private PrivacySessionChoice? _selectedSession;
    private int _redactionPresetIndex = 4;
    private int _exportFormatIndex;
    private int _retentionCountIndex = 2;
    private string _exportPreviewText = string.Empty;
    private string _backupPreviewText = string.Empty;
    private string _statusText = "Choose a stored session, redaction preset, and format.";
    private bool _isExportConfirmed;
    private bool _isSensitiveBackupConfirmed;
    private bool _isRetentionConfirmed;
    private SessionExportPreview? _exportPreview;
    private BackupPreview? _backupPreview;

    /// <summary>Creates the privacy workspace over the local store.</summary>
    public PrivacyViewModel(ProfileStore store, Action<string> reportError, Func<long?>? activeSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reportError);
        _store = store;
        _reportError = reportError;
        _activeSessionId = activeSessionId ?? (static () => null);
        RefreshSessions();
    }

    /// <summary>Locally stored session choices, newest first.</summary>
    public ObservableCollection<PrivacySessionChoice> Sessions { get; } = [];

    /// <summary>Redaction choices: raw, tokens, network identifiers, paths, and all.</summary>
    public IReadOnlyList<string> RedactionPresetChoices { get; } =
        ["None (raw)", "Tokens only", "IPs and MACs", "File paths", "Share-safe (all)"];

    /// <summary>Plaintext and deterministic JSON choices.</summary>
    public IReadOnlyList<string> ExportFormatChoices { get; } = ["Plaintext (.txt)", "JSON (.json)"];

    /// <summary>Newest-session retention choices.</summary>
    public IReadOnlyList<string> RetentionCountChoices { get; } =
        ["Keep newest 1", "Keep newest 10", "Keep newest 50", "Keep newest 100"];

    /// <summary>Backup sensitivity warning shown beside the acknowledgement.</summary>
#pragma warning disable CA1822 // Instance property is required for compiled Avalonia binding.
    public string BackupWarningText => BackupService.SensitiveDataWarning;
#pragma warning restore CA1822

    /// <summary>Selected stored session.</summary>
    public PrivacySessionChoice? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                InvalidateExportPreview();
            }
        }
    }

    /// <summary>Index into <see cref="RedactionPresetChoices"/>.</summary>
    public int RedactionPresetIndex
    {
        get => _redactionPresetIndex;
        set
        {
            if (SetProperty(ref _redactionPresetIndex, value))
            {
                InvalidateExportPreview();
            }
        }
    }

    /// <summary>Index into <see cref="ExportFormatChoices"/>.</summary>
    public int ExportFormatIndex
    {
        get => _exportFormatIndex;
        set
        {
            if (SetProperty(ref _exportFormatIndex, value))
            {
                InvalidateExportPreview();
                OnPropertyChanged(nameof(ExportFileExtension));
            }
        }
    }

    /// <summary>Index into <see cref="RetentionCountChoices"/>.</summary>
    public int RetentionCountIndex
    {
        get => _retentionCountIndex;
        set
        {
            if (SetProperty(ref _retentionCountIndex, value))
            {
                IsRetentionConfirmed = false;
            }
        }
    }

    /// <summary>Exact selected-session content that will be written.</summary>
    public string ExportPreviewText
    {
        get => _exportPreviewText;
        private set => SetProperty(ref _exportPreviewText, value);
    }

    /// <summary>Exact sensitive backup JSON that will be written.</summary>
    public string BackupPreviewText
    {
        get => _backupPreviewText;
        private set => SetProperty(ref _backupPreviewText, value);
    }

    /// <summary>Current privacy operation status.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>User confirmation for the exact current export preview.</summary>
    public bool IsExportConfirmed
    {
        get => _isExportConfirmed;
        set
        {
            if (SetProperty(ref _isExportConfirmed, value))
            {
                OnPropertyChanged(nameof(CanWriteExport));
            }
        }
    }

    /// <summary>User acknowledgement that backup and restore documents are sensitive.</summary>
    public bool IsSensitiveBackupConfirmed
    {
        get => _isSensitiveBackupConfirmed;
        set
        {
            if (SetProperty(ref _isSensitiveBackupConfirmed, value))
            {
                OnPropertyChanged(nameof(CanWriteBackup));
            }
        }
    }

    /// <summary>User confirmation for the destructive retention operation.</summary>
    public bool IsRetentionConfirmed
    {
        get => _isRetentionConfirmed;
        set => SetProperty(ref _isRetentionConfirmed, value);
    }

    /// <summary>True only after previewing and accepting the exact export.</summary>
    public bool CanWriteExport => _exportPreview is not null && IsExportConfirmed;

    /// <summary>True only after previewing and acknowledging sensitive backup content.</summary>
    public bool CanWriteBackup => _backupPreview is not null && IsSensitiveBackupConfirmed;

    /// <summary>Suggested extension matching the selected export format.</summary>
    public string ExportFileExtension => ExportFormatIndex == 1 ? ".json" : ".txt";

    /// <summary>Refreshes local sessions without hardware or network access.</summary>
    public void RefreshSessions()
    {
        var selectedId = SelectedSession?.Session.Id;
        Sessions.Clear();
        foreach (var session in _store.ListSessions())
        {
            Sessions.Add(new PrivacySessionChoice(session));
        }

        SelectedSession = Sessions.FirstOrDefault(choice => choice.Session.Id == selectedId) ?? Sessions.FirstOrDefault();
        StatusText = Sessions.Count == 0 ? "No stored sessions are available." : $"{Sessions.Count} stored session(s) available locally.";
    }

    /// <summary>Creates a redacted preview from an immutable stored-event snapshot.</summary>
    public void CreateExportPreview()
    {
        if (SelectedSession is null)
        {
            _reportError("Choose a stored session before previewing an export.");
            return;
        }

        var preset = RedactionPresetIndex switch
        {
            0 => RedactionPreset.None,
            1 => RedactionPreset.Tokens,
            2 => RedactionPreset.NetworkIdentifiers,
            3 => RedactionPreset.FilePaths,
            _ => RedactionPreset.ShareSafe,
        };
        var format = ExportFormatIndex == 1 ? SessionExportFormat.Json : SessionExportFormat.PlainText;
        try
        {
            _exportPreview = SessionExportService.CreatePreview(
                SelectedSession.Session,
                _store.ListSessionEvents(SelectedSession.Session.Id),
                preset,
                format);
            ExportPreviewText = _exportPreview.Content;
            IsExportConfirmed = false;
            StatusText = "Preview ready. Review every line, then confirm to enable export.";
        }
        catch (Exception ex) when (IsExpectedStorageException(ex))
        {
            InvalidateExportPreview();
            _reportError(ex.Message);
        }
    }

    /// <summary>Writes the exact confirmed export preview to a user-chosen local path.</summary>
    public async Task WriteExportAsync(string path)
    {
        if (_exportPreview is null || !IsExportConfirmed)
        {
            throw new InvalidOperationException("Preview and confirm the selected-session export first.");
        }

        EnsureSafeDestination(path);
        await SessionExportService.WriteAsync(_exportPreview.Confirm(), path).ConfigureAwait(false);
        StatusText = "Selected session exported locally.";
    }

    /// <summary>Creates the exact full backup preview and resets its acknowledgement.</summary>
    public void CreateBackupPreview()
    {
        try
        {
            _backupPreview = BackupService.CreatePreview(_store);
            BackupPreviewText = _backupPreview.Content;
            IsSensitiveBackupConfirmed = false;
            StatusText = "Sensitive backup preview ready. Review it and acknowledge the warning.";
        }
        catch (Exception ex) when (IsExpectedStorageException(ex))
        {
            _backupPreview = null;
            BackupPreviewText = string.Empty;
            IsSensitiveBackupConfirmed = false;
            OnPropertyChanged(nameof(CanWriteBackup));
            _reportError(ex.Message);
        }
    }

    /// <summary>Writes the exact acknowledged backup preview to a local path.</summary>
    public async Task WriteBackupAsync(string path)
    {
        if (_backupPreview is null || !IsSensitiveBackupConfirmed)
        {
            throw new InvalidOperationException("Preview the backup and acknowledge its sensitive contents first.");
        }

        EnsureSafeDestination(path);
        await BackupService.WriteAsync(_backupPreview.Confirm(), path).ConfigureAwait(false);
        StatusText = "Sensitive backup saved locally.";
    }

    /// <summary>Validates and restores an acknowledged untrusted backup as new rows.</summary>
    public async Task<string> RestoreAsync(string content)
    {
        var result = await BackupService.RestoreAsync(_store, content, IsSensitiveBackupConfirmed).ConfigureAwait(false);
        RefreshSessions();
        var warning = result.Warnings.Count == 0 ? string.Empty : " " + string.Join(" ", result.Warnings);
        StatusText = $"Restored {result.ProfilesImported} profile(s) and {result.SessionsImported} session(s); " +
            $"{result.ProfilesSkipped} profile conflict(s).{warning}";
        return StatusText;
    }

    /// <summary>Applies the explicitly confirmed newest-session retention limit.</summary>
    public void ApplyRetention()
    {
        if (!IsRetentionConfirmed)
        {
            throw new InvalidOperationException("Confirm session deletion before applying retention.");
        }

        var index = Math.Clamp(RetentionCountIndex, 0, RetentionCounts.Length - 1);
        var activeSessionId = _activeSessionId();
        var removed = _store.RetainNewestSessions(RetentionCounts[index], activeSessionId);
        IsRetentionConfirmed = false;
        RefreshSessions();
        var protection = activeSessionId is null ? string.Empty : " The active terminal session was protected.";
        StatusText = $"Retention applied locally: removed {removed} older session(s).{protection}";
    }

    private void InvalidateExportPreview()
    {
        _exportPreview = null;
        ExportPreviewText = string.Empty;
        IsExportConfirmed = false;
        OnPropertyChanged(nameof(CanWriteExport));
    }

    private void EnsureSafeDestination(string path)
    {
        if (_store.IsDatabasePath(path))
        {
            throw new IOException("The active Serial Scout database and its sidecar files cannot be export destinations.");
        }
    }

    private static bool IsExpectedStorageException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or SqliteException;
}

/// <summary>Display wrapper for one locally stored session.</summary>
public sealed class PrivacySessionChoice
{
    /// <summary>Creates a display wrapper.</summary>
    public PrivacySessionChoice(SessionMetadata session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
    }

    /// <summary>Underlying local session metadata.</summary>
    public SessionMetadata Session { get; }

    /// <summary>Stable, accessible selector label.</summary>
    public string Label => $"{Session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} — {Session.PortPath} (session {Session.Id})";
}
