using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SerialScout.App.ViewModels;
using SerialScout.Core.Discovery;
using SerialScout.Core.Discovery.Macos;
using SerialScout.Core.Discovery.Windows;
using SerialScout.Core.Storage;

namespace SerialScout.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            InstallUiScheduler();
            desktop.MainWindow = new MainWindow(
                new MainViewModel(new ProfileStore(DefaultDatabasePath()), CreateDiscovery()));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Routes the headless <see cref="Ui"/> indirection through the Avalonia dispatcher
    /// so view models never touch threading APIs directly.
    /// </summary>
    private static void InstallUiScheduler()
    {
        Ui.Post = action => Dispatcher.UIThread.Post(action);
        Ui.Repeating = (action, interval) =>
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (_, _) => action();
            timer.Start();
            return () => timer.Stop();
        };
    }

    /// <summary>
    /// Local-first store location: <c>~/.local/share/serial-scout/profiles.sqlite</c> on
    /// Unix/macOS and <c>%LOCALAPPDATA%\SerialScout\profiles.sqlite</c> on Windows.
    /// </summary>
    private static string DefaultDatabasePath()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        var directory = Path.Combine(root, "serial-scout");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "profiles.sqlite");
    }

    /// <summary>Selects the discovery adapter for the current platform.</summary>
    private static ISerialDiscovery CreateDiscovery()
        => OperatingSystem.IsWindows()
            ? new WindowsSerialDiscovery()
            : new MacosSerialDiscovery();
}
