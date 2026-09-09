using SerialScout.Core.Discovery.Macos;

namespace SerialScout.Core.Tests;

public sealed class IoregParserTests
{
    [Fact]
    public void LiftsUsbIdentityFromAncestryInCh340Fixture()
    {
        var entries = IoregParser.Parse(Fixtures.IoregCh340);

        var entry = Assert.Single(entries);
        Assert.Equal("/dev/cu.usbserial-1120", entry.PortPath);
        Assert.Equal("usbserial-1120", entry.TTYDevice);
        Assert.Equal("0x1A86", entry.VendorIdHex); // 6790 decimal
        Assert.Equal("0x7523", entry.ProductIdHex); // 29987 decimal
        Assert.Equal("wch.cn", entry.Manufacturer);
        Assert.Equal("USB2.0-Serial", entry.Product);
        Assert.Equal("AB0XYZ12", entry.SerialNumber);
        Assert.Equal("IOUSBHostDevice", entry.ClassName);
    }

    [Fact]
    public void NestedPropertyBlocksDoNotLeakKeysIntoParentScope()
    {
        var entries = IoregParser.Parse(Fixtures.IoregCh340);
        var entry = Assert.Single(entries);

        // The client's own nested IOSerialBSDClientOptions block contains decoy
        // idVendor/idProduct = 1; if nested blocks leaked, the identity would differ.
        Assert.Equal("0x1A86", entry.VendorIdHex);
        Assert.Equal("0x7523", entry.ProductIdHex);
    }

    [Fact]
    public void BareClientWithoutUsbAncestryKeepsAllIdentityNull()
    {
        var entries = IoregParser.Parse(Fixtures.IoregBareClient);

        var entry = Assert.Single(entries);
        Assert.Equal("/dev/cu.debug-console", entry.PortPath);
        Assert.Null(entry.VendorIdHex);
        Assert.Null(entry.ProductIdHex);
        Assert.Null(entry.Manufacturer);
        Assert.Null(entry.Product);
        Assert.Null(entry.SerialNumber);
    }

    [Fact]
    public void NonUsbObjectsAndNoiseLinesProduceNoEntries()
    {
        Assert.Empty(IoregParser.Parse("""
            +-o ACAdapter  <class IOPMPowerSource, id 0x100000300, registered>
            {
              "IsCharging" = Yes
            }
            random noise line
            """));
    }

    [Theory]
    [InlineData("6790", "0x1A86")]
    [InlineData("0", "0x0000")]
    [InlineData("\"29987\"", "0x7523")]
    [InlineData("123456", null)] // > 0xFFFF cannot be a USB id
    [InlineData("not-decimal", null)]
    [InlineData(null, null)]
    public void UsbIdsNormalizeFromDecimalNotation(string? raw, string? expected)
    {
        Assert.Equal(expected, IoregParser.FormatUsbId(raw));
    }
}

internal static class Fixtures
{
    public static string IoregCh340 => Read("ioreg-ch340.txt");

    public static string IoregBareClient => Read("ioreg-bare-client.txt");

    private static string Read(string name)
    {
        var assembly = typeof(Fixtures).Assembly;
        var resourceName = $"SerialScout.Core.Tests.Fixtures.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded fixture '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
