using SerialScout.App.ViewModels;
using SerialScout.Core.Discovery;
using SerialScout.Core.Profiles;
using SerialScout.Core.Storage;

namespace SerialScout.App.Tests;

/// <summary>
/// Headless view-model tests for the profile editor acceptance criteria: inline
/// validation blocks bad drafts, save round-trips through the local store, and the
/// live confidence preview runs the real conservative matcher against a reference
/// device (including ambiguity from competing rules).
/// </summary>
public sealed class ProfileEditorViewModelTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static NormalizedPort ReferencePort() => new("/dev/cu.usbserial-110", ScanState.Ready, 0x1A86, 0x7523, "wch.cn", "USB Serial", "110");

    [Fact]
    public void BlankDraftFailsValidationWithReadableProblems()
    {
        using var store = new ProfileStore(":memory:");
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);

        Assert.False(editor.TryBuildDraft(out var draft));
        Assert.Null(draft);
        Assert.Contains("Name is required.", editor.ValidationText, StringComparison.Ordinal);
        Assert.Contains("Vendor id", editor.ValidationText, StringComparison.Ordinal);
        Assert.Contains("Product id", editor.ValidationText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1A86", true)]
    [InlineData("0x1A86", true)]
    [InlineData("6790", true)]
    [InlineData("FFFF", true)]
    [InlineData("1A866", false)] // five hex digits exceeds the 0..0xFFFF range
    [InlineData("xyz", false)]
    [InlineData("", false)]
    public void UsbIdAcceptsHexAndDecimalForms(string text, bool valid)
    {
        using var store = new ProfileStore(":memory:");
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);
        editor.NameText = "P";
        editor.BaudRateText = "115200";
        editor.VendorIdText = text;
        editor.ProductIdText = "7523";

        Assert.Equal(valid, editor.TryBuildDraft(out _));
    }

    [Fact]
    public async Task SavePersistsAndRebindsAsExistingProfile()
    {
        using var store = new ProfileStore(":memory:");
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);
        DeviceProfile? savedEvent = null;
        editor.Saved += (_, profile) => savedEvent = profile;

        editor.NameText = "Lab ESP32";
        editor.VendorIdText = "1A86";
        editor.ProductIdText = "7523";
        await editor.SaveCommand.ExecuteAsync();

        Assert.NotNull(savedEvent);
        Assert.True(savedEvent!.Id > 0);
        Assert.True(editor.IsExisting);
        var stored = Assert.Single(store.ListProfiles());
        Assert.Equal("Lab ESP32", stored.Name);
        Assert.Equal(0x1A86, stored.Rule.VendorId);
    }

    [Fact]
    public async Task SaveRejectsDuplicateNameWithErrorReport()
    {
        using var store = new ProfileStore(":memory:");
        store.CreateProfile(new DeviceProfile { Name = "Dup", Rule = new ProfileMatchRule(1, 1) });
        var errors = new List<string>();
        var editor = new ProfileEditorViewModel(store, errors.Add, () => FixedUtc);

        editor.NameText = "Dup";
        editor.VendorIdText = "2";
        editor.ProductIdText = "3";
        await editor.SaveCommand.ExecuteAsync();

        Assert.NotEmpty(errors);
        Assert.Contains("Dup", string.Join(" ", errors), StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewOnSelectedDeviceReportsExactWithReasons()
    {
        using var store = new ProfileStore(":memory:");
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);
        editor.SetPreviewPort(ReferencePort());
        editor.NameText = "Dongle";
        editor.VendorIdText = "0x1A86";
        editor.ProductIdText = "0x7523";
        editor.SerialFingerprint = "110";

        Assert.Contains("EXACT match -> Dongle", editor.PreviewText, StringComparison.Ordinal);
        Assert.Contains("serial-verified", editor.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewGoesAmbiguousWhenCompetingRuleTiesAndSerialMissing()
    {
        using var store = new ProfileStore(":memory:");
        // A stored rule with the same VID/PID but no serial pin will tie with the draft
        // whenever the port reports no serial -> ambiguous, never a silent exact bind.
        store.CreateProfile(new DeviceProfile
        {
            Name = "Clone",
            Rule = new ProfileMatchRule(0x1A86, 0x7523),
            LastSeenUtc = FixedUtc,
        });
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);
        editor.SetPreviewPort(new NormalizedPort("/dev/cu.usbserial-9", ScanState.Ready, 0x1A86, 0x7523));
        editor.NameText = "Mine";
        editor.VendorIdText = "0x1A86";
        editor.ProductIdText = "0x7523";

        Assert.Contains("ambiguous", editor.PreviewText, StringComparison.Ordinal);
        Assert.Contains("score-tie-at-top", editor.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewFallsBackToCurrentStoredMatchWhileDraftBroken()
    {
        using var store = new ProfileStore(":memory:");
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);
        editor.SetPreviewPort(ReferencePort());
        editor.NameText = "Dongle";
        editor.VendorIdText = "0x1A86";
        editor.ProductIdText = "not-an-id";

        Assert.Contains("Current match on", editor.PreviewText, StringComparison.Ordinal);
        Assert.Contains("no-candidates", editor.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRemovesProfileAndResetsEditor()
    {
        using var store = new ProfileStore(":memory:");
        var saved = store.CreateProfile(new DeviceProfile { Name = "Doomed", Rule = new ProfileMatchRule(9, 9) });
        Assert.NotNull(saved);
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);
        editor.LoadExisting(saved!);
        long? deletedId = null;
        editor.Deleted += (_, id) => deletedId = id;

        await editor.DeleteCommand.ExecuteAsync();

        Assert.Equal(saved!.Id, deletedId);
        Assert.Empty(store.ListProfiles());
        Assert.False(editor.IsExisting);
        Assert.Equal("(new profile)", editor.Title);
    }

    [Fact]
    public void LoadExistingRoundTripsAllFields()
    {
        using var store = new ProfileStore(":memory:");
        var saved = store.CreateProfile(new DeviceProfile
        {
            Name = "Full",
            Rule = new ProfileMatchRule(0x2341, 0x0057, "SER9", "Arduino", "Arduino SA"),
            LineSettings = new LineSettings(9600, 7, LineParity.Even, LineStopBits.Two),
            Notes = "note",
        });
        Assert.NotNull(saved);
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);

        editor.LoadExisting(saved!);

        Assert.Equal("0x2341", editor.VendorIdText);
        Assert.Equal("0x0057", editor.ProductIdText);
        Assert.Equal("SER9", editor.SerialFingerprint);
        Assert.Equal("9600", editor.BaudRateText);
        Assert.Equal(2, editor.DataBitsIndex); // 7 bits
        Assert.Equal(2, editor.ParityIndex);   // Even
        Assert.Equal(1, editor.StopBitsIndex); // Two
        Assert.Equal("note", editor.Notes);
        Assert.True(editor.IsExisting);
    }
}
