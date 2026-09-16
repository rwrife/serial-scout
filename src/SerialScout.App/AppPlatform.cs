using SerialScout.Core.Sessions;
using SerialScout.Core.Sessions.Ports;
using SerialScout.Core.Sessions.Posix;

namespace SerialScout.App;

internal static class AppPlatform
{
    public static ISerialLinkFactory CreateSerialLinkFactory()
        => CreateSerialLinkFactory(OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

    internal static ISerialLinkFactory CreateSerialLinkFactory(bool isMacOS, bool isWindows)
    {
        if (isMacOS)
        {
            return new MacosSerialLinkFactory();
        }

        if (isWindows)
        {
            return new PortsSerialLinkFactory();
        }

        throw new PlatformNotSupportedException("Serial sessions are supported on Windows and macOS.");
    }
}
