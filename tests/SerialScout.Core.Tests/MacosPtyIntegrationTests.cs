using System.Runtime.InteropServices;
using System.Text;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Sessions.Posix;

namespace SerialScout.Core.Tests;

public sealed class MacosPtyIntegrationTests
{
    [MacOSFact]
    public Task NativeDarwinLinkExchangesDataAndInterruptsOnPty()
        => RunNativeDarwinLinkExchangesDataAndInterruptsOnPty()
            .WaitAsync(TimeSpan.FromSeconds(15));

    private static async Task RunNativeDarwinLinkExchangesDataAndInterruptsOnPty()
    {
        var name = new byte[1024];
        Assert.Equal(0, OpenPty(out var master, out var slave, name, IntPtr.Zero, IntPtr.Zero));
        var api = new DarwinNativeApi();
        var pathLength = Array.IndexOf(name, (byte)0);
        var path = Encoding.UTF8.GetString(name, 0, pathLength);
        try
        {
            using var link = new MacosSerialLink(path);
            var settings = new LineSettings(19200, 7, LineParity.Even, LineStopBits.Two);
            await link.OpenAsync(settings, CancellationToken.None);

            Assert.Equal(0, api.GetTerminalSettings(slave, out var terminal));
            Assert.Equal(19200UL, terminal.InputSpeed);
            Assert.Equal(19200UL, terminal.OutputSpeed);
            Assert.Equal(DarwinConstants.CharacterSize7, terminal.ControlFlags & DarwinConstants.ControlSize);
            Assert.NotEqual(0UL, terminal.ControlFlags & DarwinConstants.LocalMode);
            Assert.NotEqual(0UL, terminal.ControlFlags & DarwinConstants.EnableReceiver);
            Assert.NotEqual(0UL, terminal.ControlFlags & DarwinConstants.ParityEnable);
            Assert.NotEqual(0UL, terminal.InputFlags & DarwinConstants.InputParityCheck);
            Assert.NotEqual(0UL, terminal.ControlFlags & DarwinConstants.TwoStopBits);
            Assert.Equal(0UL, terminal.ControlFlags & DarwinConstants.HardwareFlowControl);
            // Darwin's hosted PTY driver can accept a second open despite TIOCEXCL.
            // Request success is checked by OpenAsync; fixture tests enforce its order
            // and failure cleanup. The CI native C probe records driver behavior separately.
            // Do not infer physical USB-driver exclusivity from a pseudo-terminal.

            Assert.Equal(0, api.Close(slave));
            slave = -1;

            var inbound = new byte[] { 1, 2, 3 };
            Assert.Equal(inbound.Length, (int)api.Write(master, inbound, 0, inbound.Length));
            Assert.Equal(
                inbound,
                await link.ReadAsync(TimeSpan.FromSeconds(2), CancellationToken.None));

            var outbound = new byte[] { 4, 5, 6, 7 };
            await link.WriteAsync(outbound, CancellationToken.None);
            var masterPoll = new[]
            {
                new DarwinPollDescriptor
                {
                    Descriptor = master,
                    Events = DarwinConstants.Readable,
                },
            };
            Assert.Equal(1, api.Poll(masterPoll, 2000));
            var received = new byte[16];
            var receivedCount = api.Read(master, received, received.Length);
            Assert.Equal(outbound, received[..checked((int)receivedCount)]);

            Assert.Empty(
                await link.ReadAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None));

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var started = Environment.TickCount64;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => link.ReadAsync(TimeSpan.FromSeconds(30), cancellation.Token));
            Assert.InRange(Environment.TickCount64 - started, 0, 2000);

            var closeRead = link.ReadAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await Task.Delay(50);
            started = Environment.TickCount64;
            var close = link.CloseAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => closeRead.WaitAsync(TimeSpan.FromSeconds(2)));
            await close.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.InRange(Environment.TickCount64 - started, 0, 1000);

            using var disconnectLink = new MacosSerialLink(path);
            await disconnectLink.OpenAsync(LineSettings.Default, CancellationToken.None);
            _ = api.Close(master);
            master = -1;
            await Assert.ThrowsAsync<LinkDisconnectedException>(
                () => disconnectLink.ReadAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
        finally
        {
            if (slave >= 0)
            {
                _ = api.Close(slave);
            }

            if (master >= 0)
            {
                _ = api.Close(master);
            }
        }
    }

    private sealed class MacOSFactAttribute : FactAttribute
    {
        public MacOSFactAttribute()
        {
            if (!OperatingSystem.IsMacOS())
            {
                Skip = "Requires the native Darwin PTY and serial ABI.";
            }
        }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "openpty", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int OpenPty(
        out int master,
        out int slave,
        [Out] byte[] name,
        IntPtr terminalSettings,
        IntPtr windowSize);
}
