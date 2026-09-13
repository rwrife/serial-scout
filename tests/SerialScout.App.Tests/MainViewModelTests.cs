using SerialScout.App.ViewModels;
using SerialScout.Core.Discovery;
using SerialScout.Core.Profiles;
using SerialScout.Core.Storage;

namespace SerialScout.App.Tests;

/// <summary>
/// Shell wiring tests: selecting an exact-matched device pre-binds the terminal, the
/// session-history list reflects stored metadata (never payloads), and the error
/// banner round-trips.
/// </summary>
public sealed class MainViewModelTests : IDisposable
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly ProfileStore _store = new(":memory:");

    private static NormalizedPort ReadyPort() => new("COM9", ScanState.Ready, 0x2341, 0x0043, "Arduino SA", "Arduino Uno", "A1B2");

    [Fact]
    public async Task ScanThenSelectPreBindsTerminalToExactProfile()
    {
        var saved = _store.CreateProfile(new DeviceProfile
        {
            Name = "Uno",
            Rule = new ProfileMatchRule(0x2341, 0x0043, "A1B2"),
            LastSeenUtc = FixedUtc,
        });
        Assert.NotNull(saved);
        using var vm = new MainViewModel(_store, new FakeSerialDiscovery().Add(ReadyPort()), () => FixedUtc);

        await vm.StartAsync();
        vm.Devices.SelectedDevice = vm.Devices.Devices[0];

        Assert.Equal("COM9", vm.Terminal.PortPath);
        Assert.Equal("Profile: Uno", vm.Terminal.ProfileLabel);
        Assert.Equal(saved!.Id, vm.Terminal.ProfileId);
        Assert.Contains("Current match on COM9: EXACT match -> Uno", vm.Editor.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionHistoryShowsBoundProfileAndRunsStatus()
    {
        var profile = _store.CreateProfile(new DeviceProfile { Name = "Bound", Rule = new ProfileMatchRule(1, 2) });
        Assert.NotNull(profile);
        _store.CreateSession("COM1", FixedUtc, profile!.Id);
        _store.CreateSession("COM2", FixedUtc.AddMinutes(5));
        using var vm = new MainViewModel(_store, new FakeSerialDiscovery(), () => FixedUtc);

        await vm.StartAsync();

        var rows = vm.SessionHistory.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("COM2", rows[0].PortPath); // newest first
        Assert.Equal("(running)", rows[0].EndedLabel);
        Assert.Equal("(unbound)", rows[0].ProfileLabel);
        Assert.Equal("Bound", rows[1].ProfileLabel);
        Assert.Contains("Session on COM1", rows[1].AccessibilitySummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorBannerRoundTripsThroughClear()
    {
        using var vm = new MainViewModel(_store, new FakeSerialDiscovery(), () => FixedUtc);

        vm.LastError = "disk full";
        Assert.Equal("disk full", vm.LastError);
        vm.ClearError();
        Assert.Equal(string.Empty, vm.LastError);
    }

    public void Dispose() => _store.Dispose();
}
