using System.Text;
using SerialScout.App.ViewModels;
using SerialScout.Core.Profiles;

namespace SerialScout.App.Tests;

/// <summary>
/// Headless view-model tests for the terminal pane (issue #5): connect/disconnect/
/// reconnect commands, engine-event driven status text, send framing, filtered log
/// rendering, and the save-log entry point.
/// </summary>
public sealed class TerminalViewModelTests
{
    private const string Port = "/dev/ttyTEST0";

    private static readonly byte[] HelloRx = "hello"u8.ToArray();
    private static readonly byte[] AtCrlf = "AT\r\n"u8.ToArray();

    private static Func<Task> WaitUntil(Func<bool> condition) => async () =>
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("Condition never became true.");
    };

    [Fact]
    public async Task ConnectSurfacesConnectedStatusAndSendFramesText()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var factory = new ScriptedLinkFactory();
        using var vm = new TerminalViewModel(factory, store, _ => { });
        vm.PortPath = Port;
        vm.LineEndingIndex = 3; // CRLF

        await vm.ConnectCommand.ExecuteAsync();
        await WaitUntil(() => vm.StatusText == $"[connected] {Port}")();

        vm.SendText = "AT";
        await vm.SendCommand.ExecuteAsync();

        var link = Assert.Single(factory.Created);
        Assert.Equal(AtCrlf, Assert.Single(link.Writes));

        vm.RefreshLog();
        Assert.Contains("TX AT", vm.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceivedTrafficRendersWithRxBadgeAndFilterApplies()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var factory = new ScriptedLinkFactory();
        using var vm = new TerminalViewModel(factory, store, _ => { });
        vm.PortPath = Port;

        await vm.ConnectCommand.ExecuteAsync();
        await WaitUntil(() => vm.StatusText.StartsWith("[connected]", StringComparison.Ordinal))();
        var link = Assert.Single(factory.Created);
        link.Push(HelloRx);
        await WaitUntil(() =>
        {
            vm.RefreshLog();
            return vm.LogText.Length > 0;
        })();

        Assert.Contains("RX hello", vm.LogText, StringComparison.Ordinal);

        vm.LogFilter = "nope";
        Assert.DoesNotContain("hello", vm.LogText, StringComparison.Ordinal);
        vm.LogFilter = "HELL";
        Assert.Contains("RX hello", vm.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedConnectSurfacesFailureAndAllowsRetry()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var factory = new ScriptedLinkFactory(path => new ScriptedLink(path)
        {
            OpenFault = new IOException("permission denied"),
        });
        using var vm = new TerminalViewModel(factory, store, _ => { });
        vm.PortPath = Port;

        await vm.ConnectCommand.ExecuteAsync();

        Assert.Contains("[failed] Connect failed", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("permission denied", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsConnected);
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisconnectStopsSessionAndUpdatesStatus()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var factory = new ScriptedLinkFactory();
        using var vm = new TerminalViewModel(factory, store, _ => { });
        vm.PortPath = Port;
        await vm.ConnectCommand.ExecuteAsync();
        await WaitUntil(() => vm.IsConnected)();

        await vm.DisconnectCommand.ExecuteAsync();

        Assert.Equal("[stopped] Disconnected.", vm.StatusText);
        Assert.False(vm.IsConnected);

        // The session metadata row must be closed in the local store.
        var session = Assert.Single(store.ListSessions());
        Assert.NotNull(session.EndedUtc);
    }

    [Fact]
    public async Task ReconnectAfterFailureStartsFreshLink()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        var first = true;
        var factory = new ScriptedLinkFactory(path =>
        {
            var link = new ScriptedLink(path);
            if (first)
            {
                link.OpenFault = new IOException("busy");
                first = false;
            }

            return link;
        });
        using var vm = new TerminalViewModel(factory, store, _ => { });
        vm.PortPath = Port;
        await vm.ConnectCommand.ExecuteAsync();
        Assert.Contains("[failed]", vm.StatusText, StringComparison.Ordinal);

        await vm.ReconnectCommand.ExecuteAsync();
        await WaitUntil(() => vm.IsConnected)();

        Assert.Equal("[connected] " + Port, vm.StatusText);
        Assert.Equal(2, factory.Created.Count); // first failed, second succeeded
    }

    [Fact]
    public async Task SaveLogWritesRenderedTextToFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-log-{Guid.NewGuid():N}.txt");
        try
        {
            using var store = new Core.Storage.ProfileStore(":memory:");
            var factory = new ScriptedLinkFactory();
            using var vm = new TerminalViewModel(factory, store, _ => { });
            vm.PortPath = Port;
            await vm.ConnectCommand.ExecuteAsync();
            vm.SendText = "AT";
            await vm.SendCommand.ExecuteAsync();

            await vm.SaveLogToFileAsync(path, _ => throw new InvalidOperationException("save must not error"));

            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("TX AT", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BindDeviceAdoptsProfileDefaults()
    {
        using var store = new Core.Storage.ProfileStore(":memory:");
        using var vm = new TerminalViewModel(new ScriptedLinkFactory(), store, _ => { });

        vm.BindDevice(Port, new DeviceProfile
        {
            Id = 5,
            Name = "Lab",
            Rule = new ProfileMatchRule(1, 2),
            LineSettings = new LineSettings(9600),
        });

        Assert.Equal(Port, vm.PortPath);
        Assert.Equal("Profile: Lab", vm.ProfileLabel);
        Assert.Equal(0, vm.BaudIndex); // 9600 is choice index 0
        _ = Encoding.UTF8;
    }
}
