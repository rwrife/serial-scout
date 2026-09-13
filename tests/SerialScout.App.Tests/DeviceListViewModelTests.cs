using SerialScout.App.ViewModels;
using SerialScout.Core.Discovery;
using SerialScout.Core.Profiles;

namespace SerialScout.App.Tests;

/// <summary>
/// Headless view-model tests for the issue-#5 acceptance criteria: device rows always
/// carry non-color-only status text, scans join discovery results with conservative
/// profile matches, and selection offers a profile draft seeded from the sighting.
/// </summary>
public sealed class DeviceListViewModelTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static NormalizedPort ReadyPort() => new("COM7", ScanState.Ready, 0x1A86, 0x7523, "wch.cn", "USB Serial", "110");

    [Fact]
    public async Task ScanProducesRowsWithTextStatusBadges()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var discovery = new FakeSerialDiscovery().Add(ReadyPort());
        var vm = new DeviceListViewModel(discovery, store, _ => { }, () => FixedUtc);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Devices);
        Assert.Equal("COM7", row.PortPath);
        Assert.Equal("[ok] Ready", row.StateLabel);
        Assert.Equal("1A86:7523", row.IdentityLabel);
        Assert.Contains("COM7", row.AccessibilitySummary, StringComparison.Ordinal);
        Assert.Contains("[ok] Ready", row.AccessibilitySummary, StringComparison.Ordinal);
        Assert.Equal("Scan complete: 1 device(s) (fake).", vm.StatusText);
    }

    [Fact]
    public async Task ScanJoinsExactProfileMatchIntoRows()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var saved = store.CreateProfile(new DeviceProfile
        {
            Name = "Lab dongle",
            Rule = new ProfileMatchRule(0x1A86, 0x7523, "110"),
            LastSeenUtc = FixedUtc,
        });
        Assert.NotNull(saved);
        var discovery = new FakeSerialDiscovery().Add(ReadyPort());
        var vm = new DeviceListViewModel(discovery, store, _ => { }, () => FixedUtc);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Devices);
        Assert.Equal("Lab dongle", row.MatchedProfileName);
        Assert.Equal("profile: Lab dongle (exact)", row.MatchLabel);
    }

    [Fact]
    public async Task SelectionOffersDraftSeededFromDevice()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var discovery = new FakeSerialDiscovery().Add(ReadyPort());
        var vm = new DeviceListViewModel(discovery, store, _ => { }, () => FixedUtc);
        await vm.RefreshAsync();
        vm.SelectedDevice = vm.Devices[0];

        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);
        Assert.True(vm.TryCreateDraftFromSelection(editor));

        Assert.Equal("0x1A86", editor.VendorIdText);
        Assert.Equal("0x7523", editor.ProductIdText);
        Assert.Equal("110", editor.SerialFingerprint);
        Assert.True(editor.TryBuildDraft(out var draft));
        Assert.NotNull(draft);
        Assert.Equal(0x1A86, draft.Rule.VendorId);
        Assert.Equal("110", draft.Rule.SerialFingerprint);
    }

    [Fact]
    public void EmptySelectionFailsDraftCreation()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var vm = new DeviceListViewModel(new FakeSerialDiscovery(), store, _ => { }, () => FixedUtc);
        var editor = new ProfileEditorViewModel(store, _ => { }, () => FixedUtc);

        Assert.False(vm.TryCreateDraftFromSelection(editor));
    }
}
