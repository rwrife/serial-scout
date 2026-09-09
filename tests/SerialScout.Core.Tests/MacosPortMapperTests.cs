using SerialScout.Core.Discovery;
using SerialScout.Core.Discovery.Macos;

namespace SerialScout.Core.Tests;

public sealed class MacosPortMapperTests
{
    private static readonly MacosSerialEntry UsbEntry = new(
        PortPath: "/dev/cu.usbmodem101",
        TTYDevice: "usbmodem101",
        VendorIdHex: "0x2341",
        ProductIdHex: "0x0043",
        Manufacturer: "Arduino LLC",
        Product: "Arduino MKR WiFi 1010",
        SerialNumber: "E8DB41EC1E38",
        ClassName: "AppleUSBACMData");

    [Fact]
    public void MapsUsbEntryWithAvailableProbeIntoRawRecord()
    {
        var raw = MacosPortMapper.ToRawRecord(UsbEntry, MacosProbeResult.Available);

        Assert.NotNull(raw);
        Assert.Equal("/dev/cu.usbmodem101", raw.PortPath);
        Assert.Equal(0x2341, PortNormalizer.ParseUsbId(raw.VendorId));
        Assert.Equal(0x0043, PortNormalizer.ParseUsbId(raw.ProductId));
        Assert.Equal("Arduino LLC", raw.Manufacturer);
        Assert.Equal(RawPortAvailability.Available, raw.Availability);
    }

    [Theory]
    [InlineData(MacosProbeResult.Available, RawPortAvailability.Available)]
    [InlineData(MacosProbeResult.Locked, RawPortAvailability.InUse)]
    [InlineData(MacosProbeResult.PermissionDenied, RawPortAvailability.AccessDenied)]
    [InlineData(MacosProbeResult.NotProbed, RawPortAvailability.Unknown)]
    [InlineData(MacosProbeResult.OtherError, RawPortAvailability.Unknown)]
    public void ProbeResultsMapToRawAvailability(MacosProbeResult probe, RawPortAvailability expected)
    {
        var raw = MacosPortMapper.ToRawRecord(UsbEntry, probe);
        Assert.NotNull(raw);
        Assert.Equal(expected, raw.Availability);
    }

    [Theory]
    [InlineData(0, MacosProbeResult.Available)]
    [InlineData(35, MacosProbeResult.Locked)]
    [InlineData(13, MacosProbeResult.PermissionDenied)]
    [InlineData(1, MacosProbeResult.PermissionDenied)]
    [InlineData(2, MacosProbeResult.PortGone)]
    [InlineData(9, MacosProbeResult.OtherError)]
    public void ErrnoMapsConservatively(int errno, MacosProbeResult expected)
    {
        Assert.Equal(expected, MacosPortMapper.MapProbeError(errno));
    }

    [Fact]
    public void VanishedPortProducesNothing()
    {
        Assert.Null(MacosPortMapper.ToRawRecord(UsbEntry, MacosProbeResult.PortGone));
    }

    [Fact]
    public void EmptyPortPathProducesNothing()
    {
        var entry = UsbEntry with { PortPath = "  " };
        Assert.Null(MacosPortMapper.ToRawRecord(entry, MacosProbeResult.Available));
    }
}
