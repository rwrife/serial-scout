using SerialScout.Core.Discovery;
using SerialScout.Core.Discovery.Macos;

namespace SerialScout.Core.Tests;

/// <summary>
/// End-to-end macOS adapter behavior driven by the recorded ioreg fixture plus fake
/// probe outcomes, so state mapping is covered without touching real devices.
/// </summary>
public sealed class MacosSerialDiscoveryTests
{
    private static readonly string[] ExpectedPortOrder = ["/dev/cu.debug-console", "/dev/cu.usbserial-1120"];

    [Fact]
    public void AdapterNormalizesFixtureOutputEndToEnd()
    {
        var entries = IoregParser.Parse(Fixtures.IoregCh340);
        var discovery = new MacosSerialDiscovery(new FakeMacosSource(
        [
            (entries[0], MacosProbeResult.Locked),
            (new MacosSerialEntry("/dev/cu.debug-console", "uart-debugconsole", null, null, null, null, null, "IOSerialBSDClient"),
                MacosProbeResult.Available),
        ]));

        var ports = discovery.Discover();

        Assert.Equal("macos", discovery.PlatformId);
        Assert.Equal(ExpectedPortOrder, ports.Select(p => p.PortPath).ToArray());

        var board = ports.Single(p => p.PortPath == "/dev/cu.usbserial-1120");
        Assert.Equal(ScanState.Busy, board.State);
        Assert.Equal(0x1A86, board.VendorId);
        Assert.Equal(0x7523, board.ProductId);
        Assert.Equal("wch.cn", board.Manufacturer);
        Assert.Equal("AB0XYZ12", board.SerialNumber);

        var debugConsole = ports.Single(p => p.PortPath == "/dev/cu.debug-console");
        Assert.Equal(ScanState.Ready, debugConsole.State);
        Assert.Null(debugConsole.VendorId); // unknown identity stays unknown
    }

    [Fact]
    public void VanishedPortsAreDropped()
    {
        var entries = IoregParser.Parse(Fixtures.IoregCh340);
        var discovery = new MacosSerialDiscovery(new FakeMacosSource([(entries[0], MacosProbeResult.PortGone)]));

        Assert.Empty(discovery.Discover());
    }

    [Fact]
    public void UnsupportedPlatformSourceYieldsEmptyList()
    {
        var discovery = new MacosSerialDiscovery(new FakeMacosSource([], supported: false));
        Assert.Empty(discovery.Discover());
    }

    private sealed class FakeMacosSource(
        IReadOnlyList<(MacosSerialEntry Entry, MacosProbeResult Probe)> entries,
        bool supported = true) : IMacosPortSource
    {
        public bool IsSupportedPlatform => supported;

        public IReadOnlyList<(MacosSerialEntry Entry, MacosProbeResult Probe)> ReadEntries() => entries;
    }
}
