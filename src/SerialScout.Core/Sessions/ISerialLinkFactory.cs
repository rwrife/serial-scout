namespace SerialScout.Core.Sessions;

/// <summary>
/// Creates the OS-facing serial link for a port path. Production builds inject an
/// adapter over <c>System.IO.Ports.SerialPort</c>; tests inject fakes so the engine is
/// exercised on CI hosts with no hardware.
/// </summary>
public interface ISerialLinkFactory
{
    /// <summary>
    /// Creates (but does not open) a link for <paramref name="portPath"/>. A fresh
    /// instance is requested for every open and every reconnect attempt.
    /// </summary>
    ISerialLink Create(string portPath);
}
