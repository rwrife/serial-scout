using SerialScout.Core.Sessions;

namespace SerialScout.Core.Tests;

public class LineEndingsTests
{
    private static readonly LineEnding[] AllEndings =
    [
        LineEnding.None,
        LineEnding.LF,
        LineEnding.CR,
        LineEnding.CRLF,
    ];

    [Fact]
    public void ToBytesMatchesWireFormat()
    {
        Assert.Empty(LineEnding.None.ToBytes().ToArray());
        Assert.Equal(new byte[] { (byte)'\n' }, LineEnding.LF.ToBytes().ToArray());
        Assert.Equal(new byte[] { (byte)'\r' }, LineEnding.CR.ToBytes().ToArray());
        Assert.Equal(new byte[] { (byte)'\r', (byte)'\n' }, LineEnding.CRLF.ToBytes().ToArray());
    }

    [Fact]
    public void StorageNamesRoundTripCaseInsensitively()
    {
        foreach (var ending in AllEndings)
        {
            Assert.Equal(ending, LineEndings.ParseStorageName(ending.ToStorageName()));
            Assert.Equal(ending, LineEndings.ParseStorageName(ending.ToStorageName().ToUpperInvariant()));
        }
    }

    [Fact]
    public void UnknownStorageNameThrowsFormat()
    {
        Assert.Throws<FormatException>(() => LineEndings.ParseStorageName("nul"));
    }
}
