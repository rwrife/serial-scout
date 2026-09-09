using SerialScout.Core.Discovery;

namespace SerialScout.Core.Tests;

public sealed class PortNormalizerTests
{
    [Theory]
    [InlineData(RawPortAvailability.Available, ScanState.Ready)]
    [InlineData(RawPortAvailability.InUse, ScanState.Busy)]
    [InlineData(RawPortAvailability.AccessDenied, ScanState.PermissionDenied)]
    [InlineData(RawPortAvailability.Unknown, ScanState.Unknown)]
    public void AvailabilityMapsToExplicitScanState(RawPortAvailability availability, ScanState expected)
    {
        var port = PortNormalizer.Normalize(new RawPortRecord("COM1", Availability: availability));
        Assert.Equal(expected, port.State);
    }

    [Theory]
    [InlineData("0x1A86", 0x1A86)]
    [InlineData("1a86", 0x1A86)]
    [InlineData("VID_1A86", 0x1A86)]
    [InlineData("0xFFFF", 0xFFFF)]
    [InlineData(" 0000 ", 0)]
    public void UsbIdsParseConservativelyAcrossNotations(string raw, int expected)
    {
        Assert.Equal(expected, PortNormalizer.ParseUsbId(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nothex")]
    [InlineData("1A8G")]
    [InlineData("0x1A866")]
    [InlineData("-1")]
    public void UsbIdsThatCannotBeProvenReturnNull(string? raw)
    {
        Assert.Null(PortNormalizer.ParseUsbId(raw));
    }

    [Fact]
    public void NormalizeParsesIdsAndCleansText()
    {
        var port = PortNormalizer.Normalize(new RawPortRecord(
            PortPath: "  COM7  ",
            DisplayName: " USB Serial Device (COM7) ",
            VendorId: "0x1A86",
            ProductId: "7523",
            Manufacturer: "  wch.cn ",
            Product: "\tUSB2.0-Serial",
            SerialNumber: "   "));

        Assert.Equal("COM7", port.PortPath);
        Assert.Equal(0x1A86, port.VendorId);
        Assert.Equal(0x7523, port.ProductId);
        Assert.Equal("wch.cn", port.Manufacturer);
        Assert.Equal("USB2.0-Serial", port.Product);
        Assert.Null(port.SerialNumber);
        Assert.Null(port.Notes);
    }

    [Fact]
    public void NormalizeAnnotatesUnparsableIdsInNotes()
    {
        var port = PortNormalizer.Normalize(new RawPortRecord(
            PortPath: "/dev/cu.usbserial-110",
            VendorId: "????",
            ProductId: "0xZZZZ",
            Notes: "probe-note"));

        Assert.Null(port.VendorId);
        Assert.Null(port.ProductId);
        Assert.Equal("probe-note; vendor-id-unparsed; product-id-unparsed", port.Notes);
    }

    [Fact]
    public void NormalizeRejectsEmptyPortPath()
    {
        Assert.Throws<ArgumentException>(() => PortNormalizer.Normalize(new RawPortRecord("   ")));
    }

    [Fact]
    public void NormalizeAllOrdersPortPathsWithNumericAwareness()
    {
        var ports = PortNormalizer.NormalizeAll(
        [
            new RawPortRecord("COM10"),
            new RawPortRecord("COM2"),
            new RawPortRecord("/dev/cu.usbserial-110"),
            new RawPortRecord("/dev/cu.usbserial-9"),
        ]);

        Assert.Equal(ExpectedMixedNormalizerOrder, ports.Select(p => p.PortPath).ToArray());
    }

    private static readonly string[] ExpectedMixedNormalizerOrder =
    [
        "/dev/cu.usbserial-9",
        "/dev/cu.usbserial-110",
        "COM2",
        "COM10",
    ];
}
