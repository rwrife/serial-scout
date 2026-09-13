using System.Collections.ObjectModel;
using SerialScout.Core.Discovery;
using SerialScout.Core.Profiles;
using SerialScout.Core.Storage;

namespace SerialScout.App.ViewModels;

/// <summary>
/// Device list pane: runs platform discovery, joins results with conservative profile
/// matches, and exposes refresh + selection state to the view. Discovery is executed
/// through an injected <see cref="ISerialDiscovery"/> and never opens ports (the engine
/// boundary enforces the DTR/RTS board-reset rule); matches are pure and local.
/// </summary>
public sealed class DeviceListViewModel : ViewModelBase
{
    private readonly ISerialDiscovery _discovery;
    private readonly ProfileStore _store;
    private readonly ProfileMatcher _matcher;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string> _reportError;
    private DeviceRow? _selectedDevice;
    private string _statusText = "Not scanned yet.";
    private bool _isScanning;

    /// <summary>Creates the device-list view model.</summary>
    /// <param name="discovery">Platform discovery adapter to scan through.</param>
    /// <param name="store">Local profile store used for matching.</param>
    /// <param name="reportError">Receives discovery failure messages.</param>
    /// <param name="utcNow">Clock injected into the matcher so previews are deterministic.</param>
    /// <param name="matcher">Matcher instance; defaults to conservative settings.</param>
    public DeviceListViewModel(
        ISerialDiscovery discovery,
        ProfileStore store,
        Action<string> reportError,
        Func<DateTimeOffset>? utcNow = null,
        ProfileMatcher? matcher = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reportError);
        _discovery = discovery;
        _store = store;
        _reportError = reportError;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _matcher = matcher ?? new ProfileMatcher();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, _reportError);
    }

    /// <summary>Rows from the latest scan, discovery order preserved.</summary>
    public ObservableCollection<DeviceRow> Devices { get; } = [];

    /// <summary>Currently highlighted device, or <see langword="null"/>.</summary>
    public DeviceRow? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                SelectedDeviceChangedHook?.Invoke(value);
            }
        }
    }

    /// <summary>Invoked whenever <see cref="SelectedDevice"/> changes (shell wiring hook).</summary>
    public Action<DeviceRow?>? SelectedDeviceChangedHook { get; set; }

    /// <summary>Human-readable scan summary shown under the list.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>True while a scan is running.</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>Runs one discovery + match pass.</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Raised after a successful scan so the shell can offer per-device actions.</summary>
    public event EventHandler? Scanned;

    /// <summary>Runs one discovery + match pass off the UI thread.</summary>
    public async Task RefreshAsync()
    {
        IsScanning = true;
        StatusText = "Scanning for devices...";
        try
        {
            var ports = await Task.Run(_discovery.Discover).ConfigureAwait(false);
            var profiles = await Task.Run(_store.ListProfiles).ConfigureAwait(false);
            var now = _utcNow();

            Ui.Post(() =>
            {
                Devices.Clear();
                foreach (var port in ports)
                {
                    Devices.Add(new DeviceRow(port, _matcher.Match(port, profiles, now)));
                }

                StatusText = Devices.Count == 0
                    ? $"Scan complete: no serial devices found ({_discovery.PlatformId})."
                    : $"Scan complete: {Devices.Count} device(s) ({_discovery.PlatformId}).";
                Scanned?.Invoke(this, EventArgs.Empty);
            });
        }
#pragma warning disable CA1031 // Scan is best-effort background work: failures land in
        // the status banner, never fault the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Ui.Post(() => StatusText = $"[!] Scan failed: {ex.Message}");
            _reportError(ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Fills <paramref name="editor"/> with a draft rule derived from the selected device
    /// (VID/PID + serial fingerprint when known), so creating a profile from a sighting is
    /// one confirmation away while staying fully editable.
    /// </summary>
    /// <param name="editor">Editor to prefill.</param>
    /// <returns><see langword="false"/> when no device is selected.</returns>
    public bool TryCreateDraftFromSelection(ProfileEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (SelectedDevice is not DeviceRow row)
        {
            return false;
        }

        editor.LoadDraft(row.Port);
        return true;
    }
}
