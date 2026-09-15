using Avalonia;
using System;

namespace SerialScout.App;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--packaged-smoke")
        {
            return PackagedSmoke.Run(Console.Out, Console.Error);
        }

        if (args.Any(arg => arg == "--packaged-smoke"))
        {
            Console.Error.WriteLine("ERROR packaged smoke accepts no additional arguments");
            return 2;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
