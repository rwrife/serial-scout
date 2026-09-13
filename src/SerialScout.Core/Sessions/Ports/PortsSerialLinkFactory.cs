namespace SerialScout.Core.Sessions.Ports;

/// <summary>
/// Production <see cref="ISerialLinkFactory"/> producing <see cref="PortsSerialLink"/>
/// instances over <c>System.IO.Ports</c>. Select this for Windows sessions; see
/// <see cref="PortsSerialLink"/> remarks for the macOS/Linux caveat and issue #12 for
/// the native termios replacement.
/// </summary>
public sealed class PortsSerialLinkFactory : ISerialLinkFactory
{
    /// <inheritdoc />
    public ISerialLink Create(string portPath) => new PortsSerialLink(portPath);
}
