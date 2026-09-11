using System.Text;
using SerialScout.Core.Sessions;

namespace SerialScout.Core.Tests;

public class RollingSessionLogTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] AbB = ["a", "b"];
    private static readonly string[] CdEf = ["cd", "ef"];

    [Fact]
    public void AppendThenSnapshotIsOldestFirst()
    {
        var log = new RollingSessionLog(maxBytes: 16);

        log.Append(Ev("a"));
        log.Append(Ev("b"));

        var snapshot = log.Snapshot();
        Assert.Equal(AbB, snapshot.Select(Text).ToArray());
        Assert.Equal(2, log.Count);
        Assert.Equal(2, log.RetainedBytes);
    }

    [Fact]
    public void EvictsOldestUntilBudgetHolds()
    {
        var log = new RollingSessionLog(maxBytes: 4);

        log.Append(Ev("ab"));
        log.Append(Ev("cd"));
        Assert.Equal(4, log.RetainedBytes);

        log.Append(Ev("ef"));

        Assert.Equal(CdEf, log.Snapshot().Select(Text).ToArray());
        Assert.Equal(4, log.RetainedBytes);
    }

    [Fact]
    public void OversizedEventCollapsesBufferToItself()
    {
        var log = new RollingSessionLog(maxBytes: 3);

        log.Append(Ev("ab"));
        log.Append(Ev("abcdef"));

        var single = Assert.Single(log.Snapshot());
        Assert.Equal("abcdef", Text(single));
        Assert.Equal(6, log.RetainedBytes);
    }

    [Fact]
    public void EmptyEventsAreIgnored()
    {
        var log = new RollingSessionLog(maxBytes: 8);

        log.Append(new LogEvent(BaseUtc, LogEventDirection.Received, []));

        Assert.Equal(0, log.Count);
        Assert.Equal(0, log.RetainedBytes);
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void ClearResetsBufferAndByteCounter()
    {
        var log = new RollingSessionLog(maxBytes: 8);
        log.Append(Ev("ab"));
        log.Append(Ev("cd"));

        log.Clear();

        Assert.Equal(0, log.Count);
        Assert.Equal(0, log.RetainedBytes);
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void ConstructorRejectsNonPositiveBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingSessionLog(maxBytes: 0));
    }

    private static LogEvent Ev(string payload, LogEventDirection direction = LogEventDirection.Received) =>
        new(BaseUtc, direction, Encoding.UTF8.GetBytes(payload));

    private static string Text(LogEvent logEvent) => Encoding.UTF8.GetString(logEvent.Payload);
}
