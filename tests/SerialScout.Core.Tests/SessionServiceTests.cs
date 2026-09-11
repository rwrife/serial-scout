using System.Text;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Storage;

namespace SerialScout.Core.Tests;

/// <summary>
/// Integration tests for the session engine driven entirely through fake serial links
/// (loopback doubles), satisfying the issue-#4 acceptance criteria without hardware.
/// </summary>
public sealed class SessionServiceTests
{
    private const string Port = "/dev/fake-scout";

    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    private static readonly byte[] AtCrlf = "AT\r\n"u8.ToArray();

    private static readonly string[] HistoryAt = ["AT"];
    private static readonly string[] HistoryPreset = ["AT+RST\\r\\n"];
    private static readonly int[] Attempts12 = [1, 2];

    [Fact]
    public async Task OpenEmitsConnectedAndAppliesProfileLineSettings()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port);
        factory.Script(link);

        var session = await StartAsync(factory, new LineSettings(9600, 7, LineParity.Even, LineStopBits.Two));
        try
        {
            Assert.Equal(SessionState.Connected, session.State);
            Assert.Equal(new LineSettings(9600, 7, LineParity.Even, LineStopBits.Two), link.OpenedSettings);
            Assert.Equal(1, link.OpenCount);
            var events = await ReadUntilAsync(session, e => e.Type == SessionEventType.Connected);
            var connected = Assert.Single(events);
            Assert.Equal(SessionEventReasons.OpenSucceeded, connected.Reason);
            Assert.Equal(Port, connected.PortPath);
            Assert.Equal(BaseUtc, connected.Utc);
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task FailedOpenEmitsStructuredFailureAndIsReportedInResult()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port) { OpenFault = new IOException("permission denied") };
        factory.Script(link);

        var result = await SessionService.StartAsync(Options(), factory, utcNow: UtcNow);

        Assert.False(result.Succeeded);
        Assert.Equal("permission denied", result.Error);
        Assert.Equal(SessionState.Failed, result.Service.State);
        var events = await ReadUntilAsync(result.Service, e => e.Type == SessionEventType.Failed);
        var failed = Assert.Single(events);
        Assert.Equal(SessionEventReasons.OpenFailed, failed.Reason);
        Assert.Equal("permission denied", failed.Detail);
        Assert.Equal(1, link.DisposeCount);
    }

    [Fact]
    public async Task SendFramesTextWithSelectedLineEndingAndRecordsHistory()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port);
        factory.Script(link);

        var session = await StartAsync(factory, lineEnding: LineEnding.CRLF);
        try
        {
            await session.SendAsync("AT");

            Assert.Equal(AtCrlf, Assert.Single(link.Writes));
            Assert.Equal(HistoryAt, session.History.Entries(Port));
            var sentEvents = session.Log.Snapshot()
                .Where(e => e.Direction == LogEventDirection.Sent)
                .ToList();
            var sent = Assert.Single(sentEvents);
            Assert.Equal(AtCrlf, sent.Payload);
            Assert.Equal(BaseUtc, sent.Utc);
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task SendPresetUsesExplicitEscapesWithoutSessionFraming()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port);
        factory.Script(link);

        var session = await StartAsync(factory, lineEnding: LineEnding.LF);
        try
        {
            await session.SendPresetAsync(new SendPreset("reset", "AT+RST\\r\\n"));

            Assert.Equal("AT+RST\r\n"u8.ToArray(), Assert.Single(link.Writes));
            Assert.Equal(HistoryPreset, session.History.Entries(Port));
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task RawSendBypassesFramingAndHistory()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port);
        factory.Script(link);

        var session = await StartAsync(factory, lineEnding: LineEnding.CRLF);
        try
        {
            await session.SendRawAsync(new byte[] { 0x00, 0xFF });

            Assert.Equal(new byte[] { 0x00, 0xFF }, Assert.Single(link.Writes));
            Assert.Empty(session.History.Entries(Port));
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task SendWhileStoppedThrowsBeforeTouchingTheLink()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port);
        factory.Script(link);

        var session = await StartAsync(factory);
        await DisposeSession(session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync("late"));
        Assert.Empty(link.Writes); // nothing reaches the link after stop
    }

    [Fact]
    public async Task ReceivedChunksLandInLogWithInjectedTimestamps()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port);
        factory.Script(link);

        var session = await StartAsync(factory);
        try
        {
            link.PushIncoming("hello"u8.ToArray());
            var received = await WaitForAsync(() => session.Log.Snapshot()
                .Where(e => e.Direction == LogEventDirection.Received)
                .Select(e => Encoding.UTF8.GetString(e.Payload))
                .FirstOrDefault(p => p == "hello"));

            Assert.NotNull(received);
            var receivedEvents = session.Log.Snapshot()
                .Where(e => e.Direction == LogEventDirection.Received)
                .ToList();
            var receivedEvent = Assert.Single(receivedEvents);
            Assert.Equal(BaseUtc, receivedEvent.Utc);
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task LogHonoursBoundedRetentionUnderContinuousTraffic()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port);
        factory.Script(link);

        var session = await StartAsync(factory, logMaxBytes: 5);
        try
        {
            link.PushIncoming("abcde"u8.ToArray());
            link.PushIncoming("fghij"u8.ToArray());
            var survived = await WaitForAsync(() => session.Log.Snapshot()
                .Where(e => e.Direction == LogEventDirection.Received)
                .Select(e => Encoding.UTF8.GetString(e.Payload))
                .FirstOrDefault(p => p == "fghij"));

            Assert.NotNull(survived);
            Assert.DoesNotContain(session.Log.Snapshot()
                .Where(e => e.Direction == LogEventDirection.Received)
                .Select(e => Encoding.UTF8.GetString(e.Payload)), p => p == "abcde");
            Assert.True(session.Log.RetainedBytes <= 5);
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task DeviceDropDuringReadReconnectsAndResumesTraffic()
    {
        var factory = new FakeSerialLinkFactory();
        var first = new FakeSerialLink(Port);
        factory.Script(first);
        var replacement = new FakeSerialLink(Port);
        factory.Script(replacement);

        var session = await StartAsync(factory);
        try
        {
            first.ReadFault = new LinkDisconnectedException(Port);

            var events = await ReadUntilAsync(
                session, e => e.Type == SessionEventType.Connected && e.Reason == SessionEventReasons.ReconnectSucceeded);

            var attempt = Assert.Single(events, e => e.Type == SessionEventType.ReconnectAttempt);
            Assert.Equal(SessionEventReasons.DeviceGone, attempt.Reason);
            Assert.Equal(1, attempt.Attempt);
            Assert.Equal(SessionState.Connected, session.State);
            Assert.Equal(2, factory.Created.Count);
            Assert.Equal(1, replacement.OpenCount);

            replacement.PushIncoming("post-reconnect"u8.ToArray());
            var resumed = await WaitForAsync(() => session.Log.Snapshot()
                .Where(e => e.Direction == LogEventDirection.Received)
                .Select(e => Encoding.UTF8.GetString(e.Payload))
                .FirstOrDefault(p => p == "post-reconnect"));
            Assert.NotNull(resumed);
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task ReconnectAfterWriteErrorCarriesDeviceGoneReason()
    {
        var factory = new FakeSerialLinkFactory();
        var first = new FakeSerialLink(Port) { WriteFault = new LinkDisconnectedException(Port) };
        factory.Script(first);
        var replacement = new FakeSerialLink(Port);
        factory.Script(replacement);

        var session = await StartAsync(factory);
        try
        {
            await Assert.ThrowsAsync<LinkDisconnectedException>(() => session.SendAsync("boom"));

            var events = await ReadUntilAsync(
                session, e => e.Type == SessionEventType.Connected && e.Reason == SessionEventReasons.ReconnectSucceeded);

            Assert.Contains(events, e =>
                e.Type == SessionEventType.ReconnectAttempt
                && e.Reason == SessionEventReasons.DeviceGone
                && e.Attempt == 1);
            Assert.Equal(SessionState.Connected, session.State);
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task ReconnectRetriesUntilAbandonedWhenOpenKeepsFailing()
    {
        var factory = new FakeSerialLinkFactory();
        var first = new FakeSerialLink(Port);
        factory.Script(first);
        factory.Script(() => new FakeSerialLink(Port) { OpenFault = new IOException("device busy") });
        factory.Script(() => new FakeSerialLink(Port) { OpenFault = new IOException("device busy") });

        var session = await StartAsync(factory, maxReconnectAttempts: 2);
        try
        {
            first.ReadFault = new LinkDisconnectedException(Port);

            var events = await ReadUntilAsync(session, e => e.Type == SessionEventType.Failed);

            var attempts = events.Where(e => e.Type == SessionEventType.ReconnectAttempt).ToArray();
            Assert.Equal(Attempts12, attempts.Select(e => e.Attempt));
            Assert.Equal(SessionEventReasons.DeviceGone, attempts[0].Reason);
            Assert.Equal(SessionEventReasons.OpenFailed, attempts[1].Reason);
            Assert.Equal("device busy", attempts[1].Detail);
            var failed = Assert.Single(events, e => e.Type == SessionEventType.Failed);
            Assert.Equal(SessionEventReasons.ReconnectAbandoned, failed.Reason);
            Assert.Equal(SessionState.Failed, session.State);
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task AutoReconnectDisabledFailsImmediatelyAfterDrop()
    {
        var factory = new FakeSerialLinkFactory();
        var first = new FakeSerialLink(Port);
        factory.Script(first);

        var session = await StartAsync(factory, autoReconnect: false);
        try
        {
            first.ReadFault = new LinkDisconnectedException(Port);

            var events = await ReadUntilAsync(session, e => e.Type == SessionEventType.Failed);

            Assert.DoesNotContain(events, e => e.Type == SessionEventType.ReconnectAttempt);
            var failed = Assert.Single(events, e => e.Type == SessionEventType.Failed);
            Assert.Equal(SessionEventReasons.ReconnectDisabled, failed.Reason);
            Assert.Equal(SessionState.Failed, session.State);
            Assert.Single(factory.Created);
        }
        finally
        {
            await DisposeSession(session);
        }
    }

    [Fact]
    public async Task SessionMetadataRowTracksLifetimeInStore()
    {
        using var store = new ProfileStore(":memory:");
        var profile = store.CreateProfile(new DeviceProfile
        {
            Name = "scout",
            Rule = new ProfileMatchRule(0x1A86, 0x7523),
        });
        var factory = new FakeSerialLinkFactory();
        factory.Script(new FakeSerialLink(Port));

        var result = await SessionService.StartAsync(
            Options(profileId: profile.Id),
            factory,
            store,
            UtcNow);
        Assert.True(result.Succeeded);
        var session = result.Service;

        var open = Assert.Single(store.ListSessions());
        Assert.Equal(profile.Id, open.ProfileId);
        Assert.Equal(Port, open.PortPath);
        Assert.Null(open.EndedUtc);

        await session.StopAsync();

        var closed = Assert.Single(store.ListSessions());
        Assert.Equal(BaseUtc, closed.EndedUtc);
        await DisposeSession(session);
    }

    [Fact]
    public async Task CleanStopEmitsDisconnectedUserRequested()
    {
        var factory = new FakeSerialLinkFactory();
        var link = new FakeSerialLink(Port);
        factory.Script(link);

        var session = await StartAsync(factory);
        await session.StopAsync();
        await DisposeSession(session);

        var events = await DrainAsync(session);
        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Contains(events, e =>
            e.Type == SessionEventType.Connected && e.Reason == SessionEventReasons.OpenSucceeded);
        Assert.Contains(events, e =>
            e.Type == SessionEventType.Disconnected && e.Reason == SessionEventReasons.UserRequested);
        Assert.True(link.CloseCount >= 1);
        Assert.Equal(1, link.DisposeCount);
    }

    [Fact]
    public async Task StopInterruptsBlockedReadPromptly()
    {
        var factory = new FakeSerialLinkFactory();
        factory.Script(() => new FakeSerialLink(Port) { BlockWhenEmpty = true });
        var clock = new TestClock(BaseUtc);

        var result = await SessionService.StartAsync(Options(), factory, store: null, clock.Now);
        Assert.True(result.Succeeded);
        var session = result.Service;

        var stop = session.StopAsync();
        var finished = await Task.WhenAny(stop, Task.Delay(2000));
        Assert.Same(stop, finished);
        await stop;

        Assert.Equal(SessionState.Stopped, session.State);
        // Exactly two clock reads: the connected event and the disconnected event.
        Assert.Equal(2, clock.Ticks);
        Assert.Equal(BaseUtc, clock.Start);
        await DisposeSession(session);
    }

    [Fact]
    public void OptionsRejectUnusableCombinations()
    {
        Assert.Throws<ArgumentException>(() => new SessionOptions { PortPath = " " }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionOptions { PortPath = Port, LogMaxBytes = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionOptions { PortPath = Port, HistoryCapacity = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionOptions { PortPath = Port, MaxReconnectAttempts = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionOptions { PortPath = Port, ReadTimeout = TimeSpan.Zero }.Validate());
    }

    // ----- helpers -------------------------------------------------------

    private static DateTimeOffset UtcNow() => BaseUtc;

    private static SessionOptions Options(
        LineSettings? lineSettings = null,
        LineEnding lineEnding = LineEnding.LF,
        long? profileId = null,
        int logMaxBytes = SessionOptions.DefaultLogMaxBytes,
        int maxReconnectAttempts = 5,
        bool autoReconnect = true) => new()
        {
            PortPath = Port,
            LineSettings = lineSettings ?? new LineSettings(1234),
            LineEnding = lineEnding,
            ProfileId = profileId,
            LogMaxBytes = logMaxBytes,
            MaxReconnectAttempts = maxReconnectAttempts,
            AutoReconnect = autoReconnect,
            ReconnectBackoff = static _ => TimeSpan.FromMilliseconds(5),
            ReadTimeout = TimeSpan.FromMilliseconds(10),
        };

    private static async Task<SessionService> StartAsync(
        FakeSerialLinkFactory factory,
        LineSettings? lineSettings = null,
        LineEnding lineEnding = LineEnding.LF,
        int logMaxBytes = SessionOptions.DefaultLogMaxBytes,
        int maxReconnectAttempts = 5,
        bool autoReconnect = true)
    {
        var result = await SessionService.StartAsync(
            Options(lineSettings, lineEnding, null, logMaxBytes, maxReconnectAttempts, autoReconnect),
            factory,
            utcNow: UtcNow);
        Assert.True(result.Succeeded, result.Error);
        return result.Service;
    }

    private static async Task DisposeSession(SessionService session) => await session.DisposeAsync();

    private static async Task<List<SessionEvent>> ReadUntilAsync(
        SessionService session,
        Func<SessionEvent, bool> stop,
        int timeoutMs = 5000)
    {
        var events = new List<SessionEvent>();
        while (true)
        {
            var readTask = session.Events.ReadAsync().AsTask();
            var finished = await Task.WhenAny(readTask, Task.Delay(timeoutMs));
            Assert.Same(readTask, finished);
            var logEvent = await readTask;
            events.Add(logEvent);
            if (stop(logEvent))
            {
                return events;
            }
        }
    }

    private static async Task<List<SessionEvent>> DrainAsync(SessionService session)
    {
        var events = new List<SessionEvent>();
        await foreach (var logEvent in session.Events.ReadAllAsync())
        {
            events.Add(logEvent);
        }

        return events;
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, int timeoutMs = 5000)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(10);
        }

        return null;
    }

    private sealed class TestClock(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;

        public int Ticks { get; private set; }

        public DateTimeOffset Now()
        {
            Ticks++;
            return Start;
        }
    }
}
