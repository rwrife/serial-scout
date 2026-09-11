using System.Text;
using SerialScout.Core.Sessions;

namespace SerialScout.Core.Tests;

public class SendHistoryTests
{
    private static readonly string[] AB = ["a", "b"];
    private static readonly string[] AOnly = ["a"];
    private static readonly string[] AbA = ["a", "b", "a"];
    private static readonly string[] BC = ["b", "c"];
    private static readonly string[] BOnly = ["b"];

    [Fact]
    public void RecordsPerScopeInOrder()
    {
        var history = new SendHistory(capacity: 5);

        history.Record("COM1", "a");
        history.Record("COM1", "b");

        Assert.Equal(AB, history.Entries("COM1"));
        Assert.Empty(history.Entries("COM2"));
    }

    [Fact]
    public void ScopesMatchCaseInsensitively()
    {
        var history = new SendHistory(capacity: 5);

        history.Record("COM1", "a");

        Assert.Equal(AOnly, history.Entries("com1"));
    }

    [Fact]
    public void CoalescesConsecutiveDuplicatesOnly()
    {
        var history = new SendHistory(capacity: 5);

        history.Record("p", "a");
        history.Record("p", "a");
        history.Record("p", "b");
        history.Record("p", "a");

        Assert.Equal(AbA, history.Entries("p"));
    }

    [Fact]
    public void EvictsOldestBeyondCapacity()
    {
        var history = new SendHistory(capacity: 2);

        history.Record("p", "a");
        history.Record("p", "b");
        history.Record("p", "c");

        Assert.Equal(BC, history.Entries("p"));
    }

    [Fact]
    public void ClearDropsOnlyTargetScope()
    {
        var history = new SendHistory(capacity: 5);
        history.Record("p1", "a");
        history.Record("p2", "b");

        history.Clear("p1");

        Assert.Empty(history.Entries("p1"));
        Assert.Equal(BOnly, history.Entries("p2"));
    }

    [Fact]
    public void BlankScopeIsRejected()
    {
        var history = new SendHistory(capacity: 5);
        Assert.Throws<ArgumentException>(() => history.Record(" ", "x"));
    }

    [Fact]
    public void PresetExpandsKnownEscapes()
    {
        var preset = new SendPreset("reset", "AT+RST\\r\\n");

        Assert.Equal(
            new byte[] { (byte)'A', (byte)'T', (byte)'+', (byte)'R', (byte)'S', (byte)'T', (byte)'\r', (byte)'\n' },
            preset.Expand());
    }

    [Fact]
    public void PresetExpandsTab()
    {
        Assert.Equal(new byte[] { (byte)'a', (byte)'\t', (byte)'b' }, SendPreset.Unescape("a\\tb"));
    }

    [Fact]
    public void PresetKeepsUnknownAndTrailingBackslashesVerbatim()
    {
        const string Raw = "C:\\path\\";

        Assert.Equal(Encoding.UTF8.GetBytes(Raw), SendPreset.Unescape(Raw));
    }
}
