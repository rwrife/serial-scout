using System.Collections.ObjectModel;
using System.Globalization;
using SerialScout.Core.Discovery;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions.Ports;
using SerialScout.Core.Storage;

namespace SerialScout.App.ViewModels;

/// <summary>
/// Application shell view model: owns the local store and the three panes (devices,
/// profiles, terminal) and wires them together — selecting a device previews it in the
/// profile editor and offers a draft, an exact profile match pre-binds the terminal,
/// and saving/deleting profiles triggers a re-match. Everything is local: one SQLite
/// file, no network, no accounts, no telemetry.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ProfileStore _store;
    private readonly Action<string> _reportError;
    private int _selectedTabIndex;
    private string _lastError = string.Empty;

    /// <summary>
    /// Creates the shell over an opened local store and the active-platform discovery
    /// adapter. Everything is local: one SQLite file, no network, no accounts, no telemetry.
    /// </summary>
    /// <param name="store">Opened local profile store (pass <c>:memory:</c>-backed stores in tests).</param>
    /// <param name="discovery">Active-platform discovery adapter.</param>
    /// <param name="utcNow">Clock injected into matching/preview panes.</param>
    public MainViewModel(ProfileStore store, ISerialDiscovery discovery, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(discovery);
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _store = store;
        _reportError = ReportErrorCore;

        Devices = new DeviceListViewModel(discovery, _store, _reportError, _utcNow);
        Editor = new ProfileEditorViewModel(_store, _reportError, _utcNow);
        Terminal = new TerminalViewModel(new PortsSerialLinkFactory(), _store, _reportError);

        Devices.SelectedDeviceChangedHook = OnDeviceSelected;
        Devices.Scanned += (_, _) =>
        {
            RefreshSessionHistory();
            ReapplySelectionPreview();
        };
        Editor.Saved += (_, _) => _ = Devices.RefreshAsync();
        Editor.Deleted += (_, _) => _ = Devices.RefreshAsync();
        RefreshSessionHistory();
    }

    private readonly Func<DateTimeOffset> _utcNow;

    /// <summary>Device list pane.</summary>
    public DeviceListViewModel Devices { get; }

    /// <summary>Profile editor pane.</summary>
    public ProfileEditorViewModel Editor { get; }

    /// <summary>Terminal session pane.</summary>
    public TerminalViewModel Terminal { get; }

    /// <summary>Most-recent-first session history rows (metadata only, never payloads).</summary>
    public ObservableCollection<SessionHistoryRow> SessionHistory { get; } = [];

    /// <summary>Zero-based workspace tab index: 0 devices, 1 profiles, 2 terminal.</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    /// <summary>Message text of the most recent reported error (empty when none).</summary>
    public string LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    /// <summary>Clears the error banner.</summary>
    public void ClearError() => LastError = string.Empty;

    /// <summary>Runs an initial discovery scan; safe to call on the UI thread at startup.</summary>
    public Task StartAsync() => Devices.RefreshAsync();

    /// <summary>Raised for pane errors so the shell can surface a banner.</summary>
    private void ReportErrorCore(string message) => LastError = message;

    private void OnDeviceSelected(DeviceRow? row)
    {
        if (row is null)
        {
            return;
        }

        Editor.SetPreviewPort(row.Port);
        Terminal.BindDevice(row.PortPath, row.MatchedProfileName is null ? null : FindProfile(row.MatchedProfileName));
    }

    private void ReapplySelectionPreview()
    {
        if (Devices.SelectedDevice is DeviceRow row)
        {
            OnDeviceSelected(row);
        }
    }

    private DeviceProfile? FindProfile(string name)
        => _store.ListProfiles().FirstOrDefault(profile =>
            string.Equals(profile.Name, name, StringComparison.Ordinal));

    private void RefreshSessionHistory()
    {
        var rows = _store.ListSessions().Select(session =>
        {
            var profile = session.ProfileId is long id ? FindProfileById(id) : null;
            return new SessionHistoryRow(session, profile?.Name);
        }).ToList();

        SessionHistory.Clear();
        foreach (var row in rows)
        {
            SessionHistory.Add(row);
        }
    }

    private DeviceProfile? FindProfileById(long id) => _store.GetProfile(id);

    /// <inheritdoc />
    public void Dispose()
    {
        Terminal.Dispose();
        _store.Dispose();
    }
}

/// <summary>
/// One session-history row rendered for browsing: port, times, bound profile name, and
/// notes — metadata only, never session payloads (payload browsing lives in the
/// terminal log panel and, from issue #6, the export pipeline).
/// </summary>
public sealed class SessionHistoryRow
{
    /// <summary>Wraps a stored session metadata row.</summary>
    /// <param name="session">Metadata row from the local store.</param>
    /// <param name="ProfileName">Bound profile display name when the profile still exists.</param>
    public SessionHistoryRow(SessionMetadata session, string? ProfileName)
    {
        Session = session;
        this.ProfileName = ProfileName;
    }

    /// <summary>The underlying metadata row.</summary>
    public SessionMetadata Session { get; }

    /// <summary>Bound profile name, or <see langword="null"/> when unbound/deleted.</summary>
    public string? ProfileName { get; }

    /// <summary>OS port path.</summary>
    public string PortPath => Session.PortPath;

    /// <summary>Start time in local time, fixed-width text.</summary>
    public string StartedLabel => Session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>End time in local time, or the running marker.</summary>
    public string EndedLabel => Session.EndedUtc is DateTimeOffset ended
        ? ended.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        : "(running)";

    /// <summary>Profile label text (never color-only).</summary>
    public string ProfileLabel => ProfileName is { Length: > 0 } name ? name : "(unbound)";

    /// <summary>Screen-reader summary of the row.</summary>
    public string AccessibilitySummary => $"Session on {PortPath}, started {StartedLabel}, ended {EndedLabel}, {ProfileLabel}";
}
