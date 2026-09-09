using SerialScout.Core.Discovery;

namespace SerialScout.Core.Tests;

public sealed class PortPathComparerTests
{
    private static readonly string[] OutOfOrderComPorts = ["COM10", "COM1", "COM2"];

    private static readonly string[] ExpectedComOrder = ["COM1", "COM2", "COM10"];

    private static readonly string[] MixedPaths =
    [
        "/dev/cu.usbmodem14201",
        "/dev/cu.usbserial-110",
        "/dev/cu.usbmodem101",
        "/dev/cu.usbserial-11",
    ];

    private static readonly string[] ExpectedMixedOrder =
    [
        "/dev/cu.usbmodem101",
        "/dev/cu.usbmodem14201",
        "/dev/cu.usbserial-11",
        "/dev/cu.usbserial-110",
    ];

    [Fact]
    public void DigitRunsCompareNumericallyNotLexically()
    {
        var sorted = OutOfOrderComPorts
            .OrderBy(p => p, PortPathComparer.Natural)
            .ToArray();

        Assert.Equal(ExpectedComOrder, sorted);
    }

    [Fact]
    public void MixedPathsWithSameShapeOrderBySegments()
    {
        var sorted = MixedPaths
            .OrderBy(p => p, PortPathComparer.Natural)
            .ToArray();

        Assert.Equal(ExpectedMixedOrder, sorted);
    }

    [Fact]
    public void AlphabeticComparisonIsCaseInsensitiveWithOrdinalTieBreak()
    {
        Assert.True(PortPathComparer.Natural.Compare("COM1", "com1") < 0); // ordinal tie-break, stable order
        Assert.True(PortPathComparer.Natural.Compare("cu.A", "cu.B") < 0);
        Assert.True(PortPathComparer.Natural.Compare("cu.b", "cu.B") > 0); // case never reverses letters
    }

    [Fact]
    public void PrefixShorterPathSortsFirst()
    {
        Assert.True(PortPathComparer.Natural.Compare("/dev/cu.a", "/dev/cu.ab") < 0);
    }
}
