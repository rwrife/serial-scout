namespace SerialScout.Core.Sessions.Ports;

/// <summary>
/// Production <see cref="ISerialLinkFactory"/> producing <see cref="PortsSerialLink"/>
/// instances over <c>System.IO.Ports</c>. Application composition selects this on Windows only.
/// </summary>
public sealed class PortsSerialLinkFactory : ISerialLinkFactory
{
    /// <inheritdoc />
    public ISerialLink Create(string portPath) => new PortsSerialLink(portPath);
}
