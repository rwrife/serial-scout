namespace SerialScout.Core.Sessions.Posix;

/// <summary>Creates native Darwin termios serial links for macOS device paths.</summary>
public sealed class MacosSerialLinkFactory : ISerialLinkFactory
{
    /// <inheritdoc />
    public ISerialLink Create(string portPath) => new MacosSerialLink(portPath);
}
