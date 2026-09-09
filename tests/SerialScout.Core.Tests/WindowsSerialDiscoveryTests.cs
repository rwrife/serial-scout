using SerialScout.Core.Discovery;
using SerialScout.Core.Discovery.Windows;

namespace SerialScout.Core.Tests;

/// <summary>
/// End-to-end adapter behavior over fixture PnP rows, including the invariant that the
/// adapter degrades gracefully on hosts whose platform source is unavailable.
/// </summary>
public sealed class WindowsSerialDiscoveryTests
{
    private static readonly string[] ExpectedPortOrder = ["COM2", "COM10"];

    [Fact]
    public void AdapterNormalizesFixtureRowsEndToEnd()
    {
        var discovery = new WindowsSerialDiscovery(new FakeWindowsSource(
        [
            new WindowsPnpRow(
                InstanceId: @"USB\VID_1A86&PID_7523\AB0XYZ12",
                FriendlyName: "USB-SERIAL CH340 (COM10)",
                DeviceDescription: null,
                HardwareIds: [@"USB\VID_1A86&PID_7523&REV_0264"],
                Probe: WindowsProbeResult.InUse),
            new WindowsPnpRow(
                InstanceId: @"USB\VID_2341&PID_0043\7&2f1a3b&0&2",
                FriendlyName: "USB 串行设备 (COM2)",
                DeviceDescription: "USB 串行设备",
                HardwareIds: [@"USB\VID_2341&PID_0043"],
                Probe: WindowsProbeResult.Available),
        ]));

        var ports = discovery.Discover();

        Assert.Equal("windows", discovery.PlatformId);
        Assert.Equal(ExpectedPortOrder, ports.Select(p => p.PortPath).ToArray()); // natural order, not lexical
        Assert.Equal(ScanState.Ready, ports[0].State);
        Assert.Equal(0x2341, ports[0].VendorId);
        Assert.Equal(ScanState.Busy, ports[1].State);
        Assert.Equal(0x1A86, ports[1].VendorId);
        Assert.Equal(0x7523, ports[1].ProductId);
        Assert.Equal("AB0XYZ12", ports[1].SerialNumber);
    }

    [Fact]
    public void UnsupportedPlatformSourceYieldsEmptyList()
    {
        var discovery = new WindowsSerialDiscovery(new FakeWindowsSource([], supported: false));
        Assert.Empty(discovery.Discover());
    }

    private sealed class FakeWindowsSource(
        IReadOnlyList<WindowsPnpRow> rows,
        bool supported = true) : IWindowsPnpRowSource
    {
        public bool IsSupportedPlatform => supported;

        public IReadOnlyList<WindowsPnpRow> ReadRows() => rows;
    }
}
