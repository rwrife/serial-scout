using System.Globalization;
using System.Text;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Storage;

namespace SerialScout.App.ViewModels;

/// <summary>
/// Terminal session pane for one port: connect / disconnect / reconnect commands, text
/// and raw send controls, live status text, and a rendered session log with
/// case-insensitive filtering. The engine's structured lifecycle events drive all status
/// text, so what the user reads always mirrors engine truth (never color-only signaling).
/// </summary>
public sealed class TerminalViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan LogRefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly ISerialLinkFactory _linkFactory;
    private readonly ProfileStore? _store;
    private readonly Action<string> _reportError;
    private SessionService? _session;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pumpTask;
    private Action? _stopLogRefresh;
    private bool _disposed;

    private string _portPath = string.Empty;
    private string _statusText = "Disconnected.";
    private string _sendText = string.Empty;
    private string _logFilter = string.Empty;
    private string _logText = string.Empty;
    private bool _autoReconnect = true;
    private int _lineEndingIndex = 1; // LF default
    private int _baudIndex = 4; // 115200 in the shared choice list
    private long? _profileId;
    private string? _profileName;

    /// <summary>Creates the terminal pane.</summary>
    /// <param name="linkFactory">Production (or test) serial link factory.</param>
    /// <param name="store">Local store receiving session metadata rows; optional.</param>
    /// <param name="reportError">Receives connect/send failure messages.</param>
    public TerminalViewModel(ISerialLinkFactory linkFactory, ProfileStore? store, Action<string> reportError)
    {
        ArgumentNullException.ThrowIfNull(linkFactory);
        ArgumentNullException.ThrowIfNull(reportError);
        _linkFactory = linkFactory;
        _store = store;
        _reportError = reportError;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, _reportError, () => CanStartSession);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, _reportError, () => _session is not null);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync, _reportError, () => CanStartSession);
        SendCommand = new AsyncRelayCommand(SendAsync, _reportError, () => IsConnected);
    }

    /// <summary>Baud-rate choices offered by the terminal.</summary>
    public IReadOnlyList<string> BaudRateChoices { get; } = ProfileEditorViewModel.SharedBaudRateChoices;

    /// <summary>Line-ending choices in enum order (None, LF, CR, CRLF).</summary>
    public IReadOnlyList<string> LineEndingChoices { get; } = ProfileEditorViewModel.SharedLineEndingChoices;

    /// <summary>Port path targeted by connect/reconnect.</summary>
    public string PortPath
    {
        get => _portPath;
        set
        {
            if (SetProperty(ref _portPath, value))
            {
                RaiseConnectionCommands();
            }
        }
    }

    /// <summary>Human-readable connection status (text badge, engine-event driven).</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Text pending in the send box.</summary>
    public string SendText
    {
        get => _sendText;
        set => SetProperty(ref _sendText, value);
    }

    /// <summary>Case-insensitive substring filter applied to the rendered log.</summary>
    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (SetProperty(ref _logFilter, value))
            {
                RefreshLogText();
            }
        }
    }

    /// <summary>Rendered, filtered session log text (oldest first, RX/TX prefixed).</summary>
    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    /// <summary>Whether unexpected drops should auto-reconnect on the next connect.</summary>
    public bool AutoReconnect
    {
        get => _autoReconnect;
        set => SetProperty(ref _autoReconnect, value);
    }

    /// <summary>Selected index into <see cref="LineEndingChoices"/>.</summary>
    public int LineEndingIndex
    {
        get => _lineEndingIndex;
        set
        {
            if (SetProperty(ref _lineEndingIndex, value))
            {
                OnPropertyChanged(nameof(SelectedLineEnding));
            }
        }
    }

    /// <summary>The framed line ending applied to text sends.</summary>
    public LineEnding SelectedLineEnding => (LineEnding)Math.Clamp(_lineEndingIndex, 0, 3);

    /// <summary>Selected index into <see cref="BaudRateChoices"/>.</summary>
    public int BaudIndex
    {
        get => _baudIndex;
        set => SetProperty(ref _baudIndex, value);
    }

    /// <summary>Profile id whose defaults bound this session, when known.</summary>
    public long? ProfileId
    {
        get => _profileId;
        set
        {
            if (SetProperty(ref _profileId, value))
            {
                OnPropertyChanged(nameof(ProfileLabel));
            }
        }
    }

    /// <summary>Profile display name shown in the session header.</summary>
    public string? ProfileName
    {
        get => _profileName;
        set
        {
            if (SetProperty(ref _profileName, value))
            {
                OnPropertyChanged(nameof(ProfileLabel));
            }
        }
    }

    /// <summary>Header text naming the bound profile (or the unbound marker).</summary>
    public string ProfileLabel => _profileName is { Length: > 0 } name
        ? $"Profile: {name}"
        : "Profile: none";

    /// <summary>True when a live (connected or reconnecting) session exists.</summary>
    public bool IsConnected => _session is { State: SessionState.Connected or SessionState.Reconnecting };

    /// <summary>Opens the configured port and starts the engine.</summary>
    public AsyncRelayCommand ConnectCommand { get; }

    /// <summary>Stops the live session cleanly.</summary>
    public AsyncRelayCommand DisconnectCommand { get; }

    /// <summary>Starts a fresh session after a failure or disconnect.</summary>
    public AsyncRelayCommand ReconnectCommand { get; }

    /// <summary>Sends <see cref="SendText"/> framed with the selected line ending.</summary>
    public AsyncRelayCommand SendCommand { get; }

    /// <summary>True when connect/reconnect can proceed.</summary>
    public bool CanStartSession => !IsConnected && !string.IsNullOrWhiteSpace(PortPath);

    /// <summary>Binds the pane to a device (and optional exact-match profile), resetting the log.</summary>
    /// <param name="portPath">OS port path to target.</param>
    /// <param name="profile">Exact-matched profile whose defaults apply, or <see langword="null"/>.</param>
    public void BindDevice(string portPath, DeviceProfile? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portPath);
        PortPath = portPath.Trim();
        ProfileId = profile?.Id;
        ProfileName = profile?.Name;
        if (profile is not null)
        {
            var index = IndexOf(BaudRateChoices, profile.LineSettings.BaudRate);
            if (index >= 0)
            {
                BaudIndex = index;
            }
        }

        LogFilter = string.Empty;
        LogText = string.Empty;
        StatusText = $"Ready to connect to {PortPath}.";
    }

    /// <summary>Opens the configured port and starts the engine, surfacing failures via events.</summary>
    public async Task ConnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected || string.IsNullOrWhiteSpace(PortPath))
        {
            return;
        }

        await DisposeSessionAsync().ConfigureAwait(false);

        var options = new SessionOptions
        {
            PortPath = PortPath.Trim(),
            LineSettings = new LineSettings(ParseBaud(BaudRateChoices[BaudIndex])),
            LineEnding = SelectedLineEnding,
            ProfileId = ProfileId,
            AutoReconnect = AutoReconnect,
        };

        var result = await SessionService.TryStartAsync(options, _linkFactory, _store).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            Ui.Post(() =>
            {
                StatusText = $"[failed] Connect failed: {result.Error}";
                RaiseConnectionCommands();
            });
            await DisposeSessionAsync().ConfigureAwait(false);
            return;
        }

        _session = result.Service;
        _pumpCancellation = new CancellationTokenSource();
        Ui.Post(() =>
        {
            StatusText = $"[connected] {PortPath}";
            RaiseConnectionCommands();
        });
        _pumpTask = PumpEventsAsync(result.Service, _pumpCancellation.Token);
        StartLogRefresh();
    }

    /// <summary>Stops the live session (no-op when disconnected).</summary>
    public async Task DisconnectAsync()
    {
        if (_session is null)
        {
            return;
        }

        var session = _session;
        var pumpCancellation = _pumpCancellation;
        var pumpTask = _pumpTask;
        _session = null;
        _pumpCancellation = null;
        _pumpTask = null;
        pumpCancellation?.Cancel();
        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Pump swallows everything internally; this await only
            // joins it, and a fault must never block a user-visible disconnect.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        await session.StopAsync().ConfigureAwait(false);
        pumpCancellation?.Dispose();
        StopLogRefresh();
        RefreshLogText();
        Ui.Post(() =>
        {
            StatusText = "[stopped] Disconnected.";
            RaiseConnectionCommands();
        });
    }

    /// <summary>Re-runs the connect flow against the same port/profile binding.</summary>
    public Task ReconnectAsync() => ConnectAsync();

    /// <summary>Sends the send-box text framed with the selected line ending.</summary>
    public async Task SendAsync()
    {
        if (string.IsNullOrEmpty(SendText))
        {
            return;
        }

        var session = _session;
        if (session is null || !IsConnected)
        {
            Ui.Post(() => _reportError("Not connected."));
            return;
        }

        try
        {
            await session.SendAsync(SendText).ConfigureAwait(false);
            Ui.Post(() =>
            {
                SendText = string.Empty;
                RefreshLogText();
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Ui.Post(() => _reportError(ex.Message));
        }
    }

    /// <summary>
    /// Save/export entry point for the log panel: writes the currently rendered
    /// (filtered) log text to a user-chosen local path. Issue #6 replaces this with the
    /// full redaction-preset export pipeline; this keeps the UI promise of a reachable
    /// save action today.
    /// </summary>
    /// <param name="path">Destination file path chosen by the user.</param>
    /// <param name="reportError">Receives IO failure messages.</param>
    public async Task SaveLogToFileAsync(string path, Action<string> reportError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(reportError);
        var text = LogText;
        try
        {
            await File.WriteAllTextAsync(path, text, Encoding.UTF8).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Ui.Post(() => reportError(ex.Message));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pumpCancellation?.Cancel();
        _pumpCancellation?.Dispose();
        StopLogRefresh();
        var session = _session;
        _session = null;
        if (session is not null)
        {
            _ = DisposeSessionCoreAsync(session);
        }
    }

    private static int IndexOf(IReadOnlyList<string> choices, int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < choices.Count; i++)
        {
            if (choices[i] == text)
            {
                return i;
            }
        }

        return -1;
    }

    private static int ParseBaud(string text)
        => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private async Task PumpEventsAsync(SessionService session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var sessionEvent in session.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var text = sessionEvent.Type switch
                {
                    SessionEventType.Connected when sessionEvent.Reason == SessionEventReasons.ReconnectSucceeded =>
                        $"[connected] {sessionEvent.PortPath} (reconnect attempt {sessionEvent.Attempt})",
                    SessionEventType.Connected => $"[connected] {sessionEvent.PortPath}",
                    SessionEventType.Disconnected => "[stopped] Disconnected.",
                    SessionEventType.ReconnectAttempt =>
                        $"[retry] Reconnecting (attempt {sessionEvent.Attempt.ToString(CultureInfo.InvariantCulture)}): {sessionEvent.Detail ?? sessionEvent.Reason}",
                    _ => $"[failed] {sessionEvent.Reason}: {sessionEvent.Detail ?? "no detail"}",
                };
                Ui.Post(() =>
                {
                    StatusText = text;
                    RaiseConnectionCommands();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Pump stopped deliberately (disconnect/dispose).
        }
#pragma warning disable CA1031 // Top of the pump task: report through the error
        // callback so a malformed event stream can never fault the UI thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Ui.Post(() => _reportError(ex.Message));
        }
    }

    private void StartLogRefresh()
    {
        StopLogRefresh();
        _stopLogRefresh = Ui.Repeating(RefreshLog, LogRefreshInterval);
    }

    /// <summary>
    /// Re-renders the log panel from the live session snapshot, applying
    /// <see cref="LogFilter"/>. The UI timer calls this; tests can drive it directly.
    /// </summary>
    public void RefreshLog() => RefreshLogText();

    private void StopLogRefresh()
    {
        Interlocked.Exchange(ref _stopLogRefresh, null)?.Invoke();
    }

    private void RefreshLogText()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var rendered = SessionLogRenderer.Render(session.Log.Snapshot(), LogFilter);
        Ui.Post(() => LogText = rendered);
    }

    private async Task DisposeSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            await DisposeSessionCoreAsync(session).ConfigureAwait(false);
        }
    }

    private static async Task DisposeSessionCoreAsync(SessionService session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Shutdown best-effort: a leak here would only be an
        // already-failing OS handle; surfacing it as an error storm helps nobody.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void RaiseConnectionCommands()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanStartSession));
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        ReconnectCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
    }
}
