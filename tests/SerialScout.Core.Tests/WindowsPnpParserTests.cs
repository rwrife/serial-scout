using SerialScout.Core.Discovery;
using SerialScout.Core.Discovery.Windows;

namespace SerialScout.Core.Tests;

public sealed class WindowsPnpParserTests
{
    [Fact]
    public void ParsesFullPnpRowIntoRawRecord()
    {
        var row = new WindowsPnpRow(
            InstanceId: @"USB\VID_2341&PID_0043\7&2f1a3b&0&2",
            FriendlyName: "USB 串行设备 (COM7)",
            DeviceDescription: "USB 串行设备",
            HardwareIds:
            [
                @"USB\VID_2341&PID_0043&REV_0001",
                @"USB\VID_2341&PID_0043",
            ],
            Probe: WindowsProbeResult.Available);

        var raw = WindowsPnpParser.Parse(row);

        Assert.NotNull(raw);
        Assert.Equal("COM7", raw.PortPath);
        Assert.Equal("2341", raw.VendorId);
        Assert.Equal("0043", raw.ProductId);
        Assert.Null(raw.SerialNumber); // instance serial contains '&': topology, not identity
        Assert.Equal(RawPortAvailability.Available, raw.Availability);
    }

    [Fact]
    public void ExtractsStableInstanceSerialWhenPresent()
    {
        var row = new WindowsPnpRow(
            InstanceId: @"USB\VID_1A86&PID_7523\AB0XYZ12",
            FriendlyName: "USB-SERIAL CH340 (COM5)",
            DeviceDescription: null,
            HardwareIds: [@"USB\VID_1A86&PID_7523&REV_0264"]);

        var raw = WindowsPnpParser.Parse(row);

        Assert.NotNull(raw);
        Assert.Equal("COM5", raw.PortPath);
        Assert.Equal("AB0XYZ12", raw.SerialNumber);
        Assert.Equal("USB-SERIAL CH340", raw.Product);
    }

    [Fact]
    public void RowsWithoutComPortProduceNothing()
    {
        var row = new WindowsPnpRow(
            InstanceId: @"BTHENUM\{00001101-...}\LOCAL_80A9...",
            FriendlyName: "蓝牙链接上的标准串行端口",
            DeviceDescription: "Bluetooth 设备",
            HardwareIds: []);

        Assert.Null(WindowsPnpParser.Parse(row));
    }

    [Fact]
    public void VanishedPortsProduceNothing()
    {
        var row = new WindowsPnpRow(
            InstanceId: @"USB\VID_1A86&PID_7523\AB0XYZ12",
            FriendlyName: "USB-SERIAL CH340 (COM5)",
            DeviceDescription: null,
            HardwareIds: [],
            Probe: WindowsProbeResult.PortGone);

        Assert.Null(WindowsPnpParser.Parse(row));
    }

    [Theory]
    [InlineData(0, WindowsProbeResult.Available)]
    [InlineData(2, WindowsProbeResult.PortGone)]
    [InlineData(5, WindowsProbeResult.InUse)]
    [InlineData(32, WindowsProbeResult.InUse)]
    [InlineData(433, WindowsProbeResult.PortGone)]
    [InlineData(87, WindowsProbeResult.OtherError)]
    public void ProbeErrorsMapToConservativeResults(int win32Error, WindowsProbeResult expected)
    {
        Assert.Equal(expected, WindowsPnpParser.MapProbeError(win32Error));
    }

    [Fact]
    public void OtherProbeErrorsKeepStateUnknownWithNote()
    {
        var row = new WindowsPnpRow(
            InstanceId: @"USB\VID_1A86&PID_7523\AB0XYZ12",
            FriendlyName: "USB-SERIAL CH340 (COM5)",
            DeviceDescription: null,
            HardwareIds: [],
            Probe: WindowsProbeResult.OtherError);

        var raw = WindowsPnpParser.Parse(row);

        Assert.NotNull(raw);
        Assert.Equal(RawPortAvailability.Unknown, raw.Availability);
        Assert.Equal("availability-probe-failed", raw.Notes);
    }
}
