namespace SerialScout.Core.Sessions;

/// <summary>
/// Creates the OS-facing serial link for a port path. Application composition selects
/// the Windows System.IO.Ports adapter or the native macOS termios adapter.
/// </summary>
public interface ISerialLinkFactory
{
    /// <summary>
    /// Creates (but does not open) a link for <paramref name="portPath"/>. A fresh
    /// instance is requested for every open and every reconnect attempt.
    /// </summary>
    ISerialLink Create(string portPath);
}
