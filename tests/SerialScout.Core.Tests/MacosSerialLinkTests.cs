using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Sessions.Posix;

namespace SerialScout.Core.Tests;

public sealed class MacosSerialLinkTests
{
    [Fact]
    public void ConstructorRejectsEmbeddedNullPath()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new MacosSerialLink("/dev/cu.fake\0hidden", new FakeDarwinNativeApi()));

        Assert.Equal("portPath", error.ParamName);
    }

    [Fact]
    public async Task OpenAsyncValidatesSettingsBeforeOpeningDevice()
    {
        using var link = new MacosSerialLink("/dev/does-not-exist");
        var invalid = new LineSettings(parity: (LineParity)99);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => link.OpenAsync(invalid, CancellationToken.None));

        Assert.Equal("lineSettings", error.ParamName);
    }

    [Fact]
    public async Task OpenAsyncAcquiresExclusiveOwnershipBeforeConfiguring()
    {
        var api = new FakeDarwinNativeApi();
        api.SetExclusiveHandler = descriptor =>
        {
            Assert.Equal(api.SerialDescriptor, descriptor);
            Assert.Equal(0, api.GetTerminalSettingsCalls);
            return 0;
        };
        using var link = new MacosSerialLink("/dev/fake", api);

        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        Assert.Equal([api.SerialDescriptor], api.ExclusiveDescriptors);
        Assert.Equal(1, api.GetTerminalSettingsCalls);
    }

    [Fact]
    public async Task OpenAsyncClosesDescriptorOnceWhenExclusiveOwnershipFails()
    {
        var api = new FakeDarwinNativeApi { LastError = 16 };
        api.SetExclusiveHandler = _ => -1;
        using var link = new MacosSerialLink("/dev/fake", api);

        var error = await Assert.ThrowsAsync<IOException>(
            () => link.OpenAsync(LineSettings.Default, CancellationToken.None));

        Assert.Contains("TIOCEXCL", error.Message, StringComparison.Ordinal);
        Assert.Contains("errno 16", error.Message, StringComparison.Ordinal);
        Assert.Equal([api.SerialDescriptor], api.ClosedDescriptors);
        Assert.Equal(0, api.GetTerminalSettingsCalls);
    }

    [Fact]
    public void ConfigureAppliesRawLocalLineSettingsWithoutHardwareFlowControl()
    {
        var api = new FakeDarwinTermiosApi
        {
            InitialSettings = new DarwinTermios
            {
                ControlFlags = DarwinConstants.HardwareFlowControl | DarwinConstants.HangUpOnClose,
                ControlCharacters = Enumerable.Repeat((byte)42, DarwinConstants.ControlCharacterCount).ToArray(),
            },
        };

        DarwinTerminalSettings.Configure(
            api,
            descriptor: 7,
            new LineSettings(57600, 7, LineParity.Even, LineStopBits.Two));

        var flags = api.AppliedSettings.ControlFlags;
        Assert.Equal(1, api.MakeRawCalls);
        Assert.Equal(DarwinConstants.ApplyNow, api.ApplyAction);
        Assert.Equal(57600UL, api.AppliedSettings.InputSpeed);
        Assert.Equal(57600UL, api.AppliedSettings.OutputSpeed);
        Assert.Equal(0UL, flags & (DarwinConstants.HardwareFlowControl | DarwinConstants.HangUpOnClose));
        Assert.Equal(DarwinConstants.CharacterSize7, flags & DarwinConstants.ControlSize);
        Assert.NotEqual(0UL, flags & DarwinConstants.LocalMode);
        Assert.NotEqual(0UL, flags & DarwinConstants.EnableReceiver);
        Assert.NotEqual(0UL, flags & DarwinConstants.ParityEnable);
        Assert.Equal(0UL, flags & DarwinConstants.OddParity);
        Assert.NotEqual(0UL, flags & DarwinConstants.TwoStopBits);
        Assert.NotEqual(0UL, api.AppliedSettings.InputFlags & DarwinConstants.InputParityCheck);
    }

    [Theory]
    [InlineData(LineParity.Even, true)]
    [InlineData(LineParity.Odd, true)]
    [InlineData(LineParity.None, false)]
    public void ConfigureSetsInputParityCheckingToMatchParity(LineParity parity, bool expected)
    {
        var api = new FakeDarwinTermiosApi
        {
            InitialSettings = new DarwinTermios
            {
                InputFlags = DarwinConstants.InputParityCheck,
                ControlCharacters = new byte[DarwinConstants.ControlCharacterCount],
            },
        };

        DarwinTerminalSettings.Configure(
            api,
            descriptor: 7,
            new LineSettings(parity: parity));

        Assert.Equal(
            expected,
            (api.AppliedSettings.InputFlags & DarwinConstants.InputParityCheck) != 0);
    }

    [Fact]
    public async Task ReadAsyncReturnsEmptyWhenPollTimesOut()
    {
        var api = new FakeDarwinNativeApi();
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        var bytes = await link.ReadAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None);

        Assert.Empty(bytes);
        Assert.Contains(25, api.PollTimeouts);
        Assert.Equal(DarwinConstants.SerialOpenFlags, Assert.Single(api.OpenFlags));
    }

    [Fact]
    public async Task ZeroTimeoutPerformsOneNonblockingPoll()
    {
        var api = new FakeDarwinNativeApi();
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        var bytes = await link.ReadAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(bytes);
        Assert.Equal([0], api.PollTimeouts);
    }

    [Fact]
    public async Task ReadAsyncRecomputesTimeoutAfterInterruptedPoll()
    {
        var api = new FakeDarwinNativeApi { LastError = DarwinConstants.Interrupted };
        long ticks = 1000;
        var calls = 0;
        api.PollHandler = (_, timeout) =>
        {
            if (calls++ == 0)
            {
                ticks += 80;
                return -1;
            }

            ticks += timeout;
            return 0;
        };
        using var link = new MacosSerialLink("/dev/fake", api, () => ticks);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        var bytes = await link.ReadAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Empty(bytes);
        Assert.Equal([25, 20], api.PollTimeouts);
    }


    [Fact]
    public async Task WriteAsyncRetriesPartialWritesAndTryAgain()
    {
        var api = new FakeDarwinNativeApi();
        api.PollHandler = (descriptors, _) =>
        {
            descriptors[0].ReturnedEvents = DarwinConstants.Writable;
            return 1;
        };
        var writes = new List<(int Offset, int Count)>();
        var call = 0;
        api.WriteHandler = (descriptor, _, offset, count) =>
        {
            if (descriptor != api.SerialDescriptor)
            {
                return count;
            }

            writes.Add((offset, count));
            return call++ switch
            {
                0 => 2,
                1 => -1,
                _ => count,
            };
        };
        api.LastError = DarwinConstants.TryAgain;
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        await link.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, CancellationToken.None);

        Assert.Equal([(0, 5), (2, 3), (2, 3)], writes);
    }


    [Fact]
    public async Task CancellationInterruptsBlockedInfiniteWriteWithinBoundedPollSlice()
    {
        using var pollEntered = new ManualResetEventSlim();
        var api = new FakeDarwinNativeApi
        {
            PollHandler = (_, timeout) =>
            {
                Assert.InRange(timeout, 0, 25);
                pollEntered.Set();
                Thread.Sleep(timeout);
                return 0;
            },
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var write = link.WriteAsync(new byte[] { 1 }, cancellation.Token);
        Assert.True(pollEntered.Wait(TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        var started = Environment.TickCount64;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);

        Assert.InRange(Environment.TickCount64 - started, 0, 250);
    }

    [Fact]
    public async Task ReadAsyncRetriesTryAgainAndReturnsOnlyBytesRead()
    {
        var api = new FakeDarwinNativeApi { LastError = DarwinConstants.TryAgain };
        api.PollHandler = (descriptors, _) =>
        {
            descriptors[0].ReturnedEvents = DarwinConstants.Readable;
            return 1;
        };
        var calls = 0;
        api.ReadHandler = (descriptor, buffer, _) =>
        {
            if (descriptor != api.SerialDescriptor || calls++ == 0)
            {
                return -1;
            }

            buffer[0] = 7;
            buffer[1] = 8;
            buffer[2] = 9;
            return 3;
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        var bytes = await link.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(new byte[] { 7, 8, 9 }, bytes);
    }

    [Fact]
    public async Task ReadAsyncTreatsZeroByteReadAsNoData()
    {
        var api = new FakeDarwinNativeApi();
        api.PollHandler = (descriptors, _) =>
        {
            descriptors[0].ReturnedEvents = DarwinConstants.Readable;
            return 1;
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        var bytes = await link.ReadAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(bytes);
    }

    [Fact]
    public async Task ReadAsyncMapsPollHangupToDisconnected()
    {
        var api = new FakeDarwinNativeApi
        {
            PollHandler = (descriptors, _) =>
            {
                descriptors[0].ReturnedEvents = DarwinConstants.PollHangUp;
                return 1;
            },
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        var error = await Assert.ThrowsAsync<LinkDisconnectedException>(
            () => link.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal("/dev/fake", error.PortPath);
    }

    [Fact]
    public async Task ReadAsyncReturnsBufferedBytesBeforeCombinedHangupThenDisconnects()
    {
        var api = new FakeDarwinNativeApi();
        api.PollHandler = (descriptors, _) =>
        {
            descriptors[0].ReturnedEvents = DarwinConstants.Readable | DarwinConstants.PollHangUp;
            return 1;
        };
        var reads = 0;
        api.ReadHandler = (_, buffer, _) =>
        {
            if (reads++ != 0)
            {
                return 0;
            }

            buffer[0] = 0x41;
            buffer[1] = 0x42;
            return 2;
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        Assert.Equal(
            new byte[] { 0x41, 0x42 },
            await link.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        await Assert.ThrowsAsync<LinkDisconnectedException>(
            () => link.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task ReadAsyncMapsCombinedReadableHangupWithZeroReadToDisconnected()
    {
        var api = new FakeDarwinNativeApi
        {
            PollHandler = (descriptors, _) =>
            {
                descriptors[0].ReturnedEvents = DarwinConstants.Readable | DarwinConstants.PollHangUp;
                return 1;
            },
        };
        var reads = 0;
        api.ReadHandler = (_, _, _) =>
        {
            reads++;
            return 0;
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        await Assert.ThrowsAsync<LinkDisconnectedException>(
            () => link.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ReadAsyncMapsCombinedReadableHangupWithTryAgainToDisconnected()
    {
        var api = new FakeDarwinNativeApi
        {
            LastError = DarwinConstants.TryAgain,
            PollHandler = (descriptors, _) =>
            {
                descriptors[0].ReturnedEvents = DarwinConstants.Readable | DarwinConstants.PollHangUp;
                return 1;
            },
        };
        var reads = 0;
        api.ReadHandler = (_, _, _) =>
        {
            reads++;
            return -1;
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        await Assert.ThrowsAsync<LinkDisconnectedException>(
            () => link.ReadAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task WriteAsyncDoesNotWriteThroughCombinedWritableHangup()
    {
        var api = new FakeDarwinNativeApi
        {
            PollHandler = (descriptors, _) =>
            {
                descriptors[0].ReturnedEvents = DarwinConstants.Writable | DarwinConstants.PollHangUp;
                return 1;
            },
        };
        var writes = 0;
        api.WriteHandler = (_, _, _, count) =>
        {
            writes++;
            return count;
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);

        await Assert.ThrowsAsync<LinkDisconnectedException>(
            () => link.WriteAsync(new byte[] { 1 }, CancellationToken.None));

        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task CancellationInterruptsBlockedReadWithinBoundedPollSlice()
    {
        using var pollEntered = new ManualResetEventSlim();
        var api = new FakeDarwinNativeApi();
        api.PollHandler = (_, timeout) =>
        {
            Assert.InRange(timeout, 0, 25);
            pollEntered.Set();
            Thread.Sleep(timeout);
            return 0;
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var read = link.ReadAsync(TimeSpan.FromSeconds(30), cancellation.Token);
        Assert.True(pollEntered.Wait(TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        var started = Environment.TickCount64;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        Assert.InRange(Environment.TickCount64 - started, 0, 250);
    }

    [Fact]
    public async Task CloseWaitsForBlockedReadBeforeClosingSerialDescriptor()
    {
        using var pollEntered = new ManualResetEventSlim();
        var api = new FakeDarwinNativeApi();
        api.PollHandler = (_, timeout) =>
        {
            Assert.InRange(timeout, 0, 25);
            pollEntered.Set();
            Thread.Sleep(timeout);
            return 0;
        };
        using var link = new MacosSerialLink("/dev/fake", api);
        await link.OpenAsync(LineSettings.Default, CancellationToken.None);
        var read = link.ReadAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(pollEntered.Wait(TimeSpan.FromSeconds(2)));

        var close = link.CloseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await close;

        Assert.Equal(api.SerialDescriptor, api.ClosedDescriptors[^1]);
        Assert.DoesNotContain(api.ClosedDescriptors, descriptor => descriptor != api.SerialDescriptor);
    }
}
