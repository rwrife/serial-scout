using System.Globalization;
using Microsoft.Data.Sqlite;
using SerialScout.Core.Discovery;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Storage;

namespace SerialScout.App.ViewModels;

/// <summary>
/// Profile editor pane: create/edit/delete local device profiles with inline validation
/// and a live confidence preview that runs the real <see cref="ProfileMatcher"/> against
/// a reference device, so users see exactly how conservative a rule is before saving.
/// All state is plain text fields (screen-reader friendly) and every mutation path is a
/// command with an explicit error report — no silent failures.
/// </summary>
public sealed class ProfileEditorViewModel : ViewModelBase
{
    private const string NewProfileName = "(new profile)";

    private readonly ProfileStore _store;
    private readonly ProfileMatcher _matcher;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string> _reportError;

    private string _title = NewProfileName;
    private string _nameText = string.Empty;
    private string _vendorIdText = string.Empty;
    private string _productIdText = string.Empty;
    private string _serialFingerprint = string.Empty;
    private string _productHint = string.Empty;
    private string _manufacturerHint = string.Empty;
    private string _baudRateText = LineSettings.DefaultBaudRate.ToString(CultureInfo.InvariantCulture);
    private int _dataBitsIndex = 3; // index 3 == 8 data bits
    private int _parityIndex;
    private int _stopBitsIndex;
    private int _lineEndingIndex = 1; // LF default, matching SessionOptions
    private string _notes = string.Empty;
    private string _validationText = string.Empty;
    private string _previewText = "Select a scanned device to preview match confidence.";
    private bool _isExisting;
    private long _existingId;
    private NormalizedPort? _previewPort;

    /// <summary>Creates the editor over the local profile store.</summary>
    /// <param name="store">Local profile store.</param>
    /// <param name="reportError">Receives save-failure messages.</param>
    /// <param name="utcNow">Clock injected into the confidence preview.</param>
    /// <param name="matcher">Matcher used for the preview; defaults to conservative settings.</param>
    public ProfileEditorViewModel(
        ProfileStore store,
        Action<string> reportError,
        Func<DateTimeOffset>? utcNow = null,
        ProfileMatcher? matcher = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reportError);
        _store = store;
        _reportError = reportError;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _matcher = matcher ?? new ProfileMatcher();
        SaveCommand = new AsyncRelayCommand(SaveAsync, _reportError);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, _reportError);
        ResetCommand = new RelayCommand(_ => ResetToNew());
    }

    /// <summary>Baud-rate choices offered by the editor.</summary>
    public IReadOnlyList<string> BaudRateChoices { get; } = SharedBaudRateChoices;

    /// <summary>Data-bit choices offered by the editor.</summary>
    public IReadOnlyList<int> DataBitsChoices { get; } = SharedDataBitsChoices;

    /// <summary>Parity choices in enum order (None, Odd, Even).</summary>
    public IReadOnlyList<string> ParityChoices { get; } = SharedParityChoices;

    /// <summary>Stop-bit choices in enum order (One, Two).</summary>
    public IReadOnlyList<string> StopBitsChoices { get; } = SharedStopBitsChoices;

    /// <summary>Line-ending choices in enum order (None, LF, CR, CRLF).</summary>
    public IReadOnlyList<string> LineEndingChoices { get; } = SharedLineEndingChoices;

    /// <summary>Shared baud-rate choice list (instance properties above expose it for binding).</summary>
    public static IReadOnlyList<string> SharedBaudRateChoices { get; } =
        ["9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600"];

    /// <summary>Shared data-bit choice list.</summary>
    public static IReadOnlyList<int> SharedDataBitsChoices { get; } = [5, 6, 7, 8];

    /// <summary>Shared parity choice list.</summary>
    public static IReadOnlyList<string> SharedParityChoices { get; } = ["None", "Odd", "Even"];

    /// <summary>Shared stop-bit choice list.</summary>
    public static IReadOnlyList<string> SharedStopBitsChoices { get; } = ["One", "Two"];

    /// <summary>Shared line-ending choice list.</summary>
    public static IReadOnlyList<string> SharedLineEndingChoices { get; } = ["None", "LF", "CR", "CRLF"];

    /// <summary>Editor heading: the profile name when editing, a placeholder when new.</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Profile name text.</summary>
    public string NameText
    {
        get => _nameText;
        set { if (SetProperty(ref _nameText, value)) RefreshDerived(); }
    }

    /// <summary>Vendor id text: hex (<c>1A86</c>, <c>0x1A86</c>) or decimal.</summary>
    public string VendorIdText
    {
        get => _vendorIdText;
        set { if (SetProperty(ref _vendorIdText, value)) RefreshDerived(); }
    }

    /// <summary>Product id text: hex or decimal like <see cref="VendorIdText"/>.</summary>
    public string ProductIdText
    {
        get => _productIdText;
        set { if (SetProperty(ref _productIdText, value)) RefreshDerived(); }
    }

    /// <summary>Exact serial-number fingerprint (optional strong binding).</summary>
    public string SerialFingerprint
    {
        get => _serialFingerprint;
        set { if (SetProperty(ref _serialFingerprint, value)) RefreshDerived(); }
    }

    /// <summary>Optional product-name substring hint.</summary>
    public string ProductHint
    {
        get => _productHint;
        set { if (SetProperty(ref _productHint, value)) RefreshDerived(); }
    }

    /// <summary>Optional manufacturer substring hint.</summary>
    public string ManufacturerHint
    {
        get => _manufacturerHint;
        set { if (SetProperty(ref _manufacturerHint, value)) RefreshDerived(); }
    }

    /// <summary>Baud rate text (also selectable from <see cref="BaudRateChoices"/>).</summary>
    public string BaudRateText
    {
        get => _baudRateText;
        set { if (SetProperty(ref _baudRateText, value)) RefreshDerived(); }
    }

    /// <summary>Selected index into <see cref="DataBitsChoices"/>.</summary>
    public int DataBitsIndex
    {
        get => _dataBitsIndex;
        set { if (SetProperty(ref _dataBitsIndex, value)) RefreshDerived(); }
    }

    /// <summary>Selected index into <see cref="ParityChoices"/>.</summary>
    public int ParityIndex
    {
        get => _parityIndex;
        set { if (SetProperty(ref _parityIndex, value)) RefreshDerived(); }
    }

    /// <summary>Selected index into <see cref="StopBitsChoices"/>.</summary>
    public int StopBitsIndex
    {
        get => _stopBitsIndex;
        set { if (SetProperty(ref _stopBitsIndex, value)) RefreshDerived(); }
    }

    /// <summary>Selected index into <see cref="LineEndingChoices"/>.</summary>
    public int LineEndingIndex
    {
        get => _lineEndingIndex;
        set { if (SetProperty(ref _lineEndingIndex, value)) OnPropertyChanged(nameof(SelectedLineEnding)); }
    }

    /// <summary>The line ending sessions should use for this profile.</summary>
    public LineEnding SelectedLineEnding => (LineEnding)Math.Clamp(_lineEndingIndex, 0, 3);

    /// <summary>Free-form notes.</summary>
    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    /// <summary>Joined validation problems, empty when the draft is saveable.</summary>
    public string ValidationText
    {
        get => _validationText;
        private set => SetProperty(ref _validationText, value);
    }

    /// <summary>Live confidence preview text produced by the real matcher.</summary>
    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    /// <summary>True when editing a stored profile (enables delete).</summary>
    public bool IsExisting
    {
        get => _isExisting;
        private set { if (SetProperty(ref _isExisting, value)) DeleteCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Persists the validated draft (create or update).</summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>Deletes the stored profile being edited.</summary>
    public AsyncRelayCommand DeleteCommand { get; }

    /// <summary>Clears the editor back to a blank new-profile draft.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>Raised after a successful save with the stored profile.</summary>
    public event EventHandler<DeviceProfile>? Saved;

    /// <summary>Raised after a successful delete with the removed profile id.</summary>
    public event EventHandler<long>? Deleted;

    /// <summary>Resets the editor to a blank new-profile draft.</summary>
    public void ResetToNew()
    {
        Title = NewProfileName;
        NameText = string.Empty;
        VendorIdText = string.Empty;
        ProductIdText = string.Empty;
        SerialFingerprint = string.Empty;
        ProductHint = string.Empty;
        ManufacturerHint = string.Empty;
        BaudRateText = LineSettings.DefaultBaudRate.ToString(CultureInfo.InvariantCulture);
        DataBitsIndex = 3;
        ParityIndex = 0;
        StopBitsIndex = 0;
        LineEndingIndex = 1;
        Notes = string.Empty;
        _existingId = 0;
        IsExisting = false;
        _previewPort = null;
        PreviewText = "Select a scanned device to preview match confidence.";
        RefreshDerived();
    }

    /// <summary>Loads a stored profile into the editor for modification.</summary>
    /// <param name="profile">Profile to edit.</param>
    public void LoadExisting(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Title = profile.Name;
        NameText = profile.Name;
        VendorIdText = ToHex(profile.Rule.VendorId);
        ProductIdText = ToHex(profile.Rule.ProductId);
        SerialFingerprint = profile.Rule.SerialFingerprint ?? string.Empty;
        ProductHint = profile.Rule.ProductHint ?? string.Empty;
        ManufacturerHint = profile.Rule.ManufacturerHint ?? string.Empty;
        BaudRateText = profile.LineSettings.BaudRate.ToString(CultureInfo.InvariantCulture);
        DataBitsIndex = Array.IndexOf(DataBitsChoices.ToArray(), profile.LineSettings.DataBits);
        ParityIndex = (int)profile.LineSettings.Parity;
        StopBitsIndex = (int)profile.LineSettings.StopBits;
        Notes = profile.Notes ?? string.Empty;
        _existingId = profile.Id;
        IsExisting = true;
        RefreshDerived();
    }

    /// <summary>
    /// Prefills a new-profile draft from a discovered device: VID/PID are copied verbatim
    /// and the serial (when the OS exposed one) is pinned as the fingerprint.
    /// </summary>
    /// <param name="port">Discovered device to seed the draft from.</param>
    public void LoadDraft(NormalizedPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        ResetToNew();
        _previewPort = port;
        VendorIdText = port.VendorId is int vendor ? ToHex(vendor) : string.Empty;
        ProductIdText = port.ProductId is int product ? ToHex(product) : string.Empty;
        SerialFingerprint = port.SerialNumber ?? string.Empty;
        ProductHint = port.Product ?? string.Empty;
        ManufacturerHint = port.Manufacturer ?? string.Empty;
        NameText = port.DisplayName is { Length: > 0 } display ? display : port.PortPath;
        PreviewText = "Preview updates as you edit: no valid rule yet.";
        RefreshDerived();
    }

    /// <summary>
    /// Builds the draft profile from the current editor state.
    /// </summary>
    /// <param name="profile">The validated draft when the return value is <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when validation fails; <see cref="ValidationText"/> explains why.</returns>
    public bool TryBuildDraft(out DeviceProfile? profile)
    {
        profile = null;
        var problems = new List<string>(4);

        if (string.IsNullOrWhiteSpace(NameText))
        {
            problems.Add("Name is required.");
        }

        if (!TryParseUsbId(VendorIdText, out var vendorId))
        {
            problems.Add("Vendor id must be 1-4 hex digits or a decimal 0-65535.");
        }

        if (!TryParseUsbId(ProductIdText, out var productId))
        {
            problems.Add("Product id must be 1-4 hex digits or a decimal 0-65535.");
        }

        if (!int.TryParse(BaudRateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud)
            || baud <= 0)
        {
            problems.Add("Baud rate must be a positive number.");
        }

        if (DataBitsIndex < 0 || DataBitsIndex >= DataBitsChoices.Count)
        {
            problems.Add("Data bits must be 5, 6, 7, or 8.");
        }

        ValidationText = string.Join(" ", problems);
        if (problems.Count > 0)
        {
            return false;
        }

        try
        {
            profile = new DeviceProfile
            {
                Id = _existingId,
                Name = NameText.Trim(),
                Rule = new ProfileMatchRule(
                    vendorId!.Value,
                    productId!.Value,
                    SerialFingerprint,
                    ProductHint,
                    ManufacturerHint),
                LineSettings = new LineSettings(
                    baud,
                    DataBitsChoices[DataBitsIndex],
                    (LineParity)ParityIndex,
                    (LineStopBits)StopBitsIndex),
                Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            };
        }
#pragma warning disable CA1031 // Model construction guards ranges; report as validation,
        // never let an out-of-range control index escape a UI command.
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException)
#pragma warning restore CA1031
        {
            ValidationText = ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>Sets the device the confidence preview matches against.</summary>
    /// <param name="port">Reference device, or <see langword="null"/> to clear the preview.</param>
    public void SetPreviewPort(NormalizedPort? port)
    {
        _previewPort = port;
        RefreshDerived();
    }

    private async Task SaveAsync()
    {
        if (!TryBuildDraft(out var draft) || draft is null)
        {
            return;
        }

        var saved = await Task.Run(() =>
        {
            try
            {
                return _isExisting
                    ? _store.UpdateProfile(draft) ? _store.GetProfile(draft.Id) : null
                    : _store.CreateProfile(draft);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // UNIQUE constraint: the profile name is taken. Surface it as a
                // validation-style message instead of a raw SQLite error.
                Ui.Post(() => _reportError($"A profile named '{draft.Name}' already exists."));
                return null;
            }
        }).ConfigureAwait(false);

        if (saved is null)
        {
            Ui.Post(() => _reportError($"Profile '{draft.Name}' no longer exists; it was not updated."));
            return;
        }

        Ui.Post(() =>
        {
            LoadExisting(saved);
            Saved?.Invoke(this, saved);
        });
    }

    private async Task DeleteAsync()
    {
        if (!_isExisting)
        {
            return;
        }

        var id = _existingId;
        var removed = await Task.Run(() => _store.DeleteProfile(id)).ConfigureAwait(false);
        Ui.Post(() =>
        {
            if (removed)
            {
                ResetToNew();
                Deleted?.Invoke(this, id);
            }
            else
            {
                _reportError($"Profile #{id.ToString(CultureInfo.InvariantCulture)} no longer exists.");
            }
        });
    }

    private void RefreshDerived()
    {
        // Validation must refresh on every keystroke even before a preview device is
        // chosen, so the Save button never silently operates on a broken draft.
        TryBuildDraft(out _);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_previewPort is not NormalizedPort port)
        {
            return;
        }

        if (!TryBuildDraft(out var draft) || draft is null)
        {
            // No draft yet (or the draft is broken): report how the *stored* profiles
            // currently identify this device instead of staying silent — the panel
            // always tells the truth about the selected device.
            var stored = _store.ListProfiles();
            var current = _matcher.Match(port, stored, _utcNow());
            var currentReasons = string.Join(", ", current.Reasons);
            var summary = current.Confidence switch
            {
                MatchConfidence.Exact => $"EXACT match -> {current.Profile?.Name}",
                MatchConfidence.Ambiguous => "ambiguous",
                _ => "no match",
            };
            PreviewText = $"Current match on {port.PortPath}: {summary} ({currentReasons})";
            return;
        }

        // The preview matches the draft against the reference device alongside every
        // stored profile, so users see real competition and staleness effects, not a
        // toy scoring pass. The draft counts as seen now: the user is creating it from
        // a live sighting, so recency should never demote the draft itself.
        var candidates = _store.ListProfiles().Where(stored => stored.Id != draft.Id).ToList();
        candidates.Add(draft with { LastSeenUtc = _utcNow() });
        var result = _matcher.Match(port, candidates, _utcNow());
        var reasons = new List<string>(result.Reasons);
        if (result.Candidates.Count > 0)
        {
            reasons.AddRange(result.Candidates[0].Reasons);
        }

        var reasonText = string.Join(", ", reasons);
        PreviewText = result.Confidence switch
        {
            MatchConfidence.Exact => $"Preview on {port.PortPath}: EXACT match -> {draft.Name} ({reasonText})",
            MatchConfidence.Ambiguous => $"Preview on {port.PortPath}: ambiguous ({reasonText})",
            _ => $"Preview on {port.PortPath}: no match ({reasonText})",
        };
    }

    private static string ToHex(int value)
        => "0x" + value.ToString("X4", CultureInfo.InvariantCulture);

    private static bool TryParseUsbId(string? text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var isHex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (isHex)
        {
            trimmed = trimmed[2..];
        }
        else if (trimmed.All(Uri.IsHexDigit))
        {
            // Bare digits are ambiguous between hex and decimal; 1-4 digit values with
            // any a-f letter are unambiguous hex, otherwise pure digits parse as hex
            // only when the user prefixed 0x, else decimal — matching how OS tooling
            // (Device Manager vs ioreg) presents the same id.
            isHex = trimmed.Any(char.IsAsciiLetter);
        }

        var parsed = isHex
            ? int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : (int?)null
            : int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec <= 0xFFFF ? dec : (int?)null;

        if (parsed is null || parsed < 0 || parsed > 0xFFFF)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
