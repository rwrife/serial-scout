using SerialScout.Core.Profiles;

namespace SerialScout.Core.Sessions;

/// <summary>
/// OS-independent view of one opened serial link. The production implementation wraps
/// <c>System.IO.Ports.SerialPort</c>; tests inject a fake so the whole session engine
/// runs on Linux CI hosts without real hardware.
/// </summary>
public interface ISerialLink : IDisposable
{
    /// <summary>OS port path, e.g. <c>COM7</c> or <c>/dev/cu.usbserial-110</c>.</summary>
    string PortPath { get; }

    /// <summary>Line settings the link was last successfully configured with.</summary>
    LineSettings LineSettings { get; }

    /// <summary>
    /// Applies line settings and marks the link open. Implementations must fully open
    /// the device here (this is the explicit user connect action, unlike discovery).
    /// </summary>
    Task OpenAsync(LineSettings lineSettings, CancellationToken cancellationToken);

    /// <summary>Writes already-framed raw bytes to the device.</summary>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Reads available bytes, waiting up to <paramref name="timeout"/> for at least one
    /// byte. Returns an empty array when the timeout expires with no data.
    /// </summary>
    Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Closes the link. Implementations must tolerate double-close.</summary>
    Task CloseAsync();
}

/// <summary>
/// Raised when the OS reports the device vanished while a link was open (cable pulled,
/// USB sleep). Implementations should raise this at most once per link.
/// </summary>
public class LinkDisconnectedException : IOException
{
    /// <summary>Creates the exception with no port context (serialization-friendly default).</summary>
    public LinkDisconnectedException()
        : base("Serial device was disconnected.")
    {
        PortPath = string.Empty;
    }

    /// <summary>Creates the exception for a given port path.</summary>
    /// <param name="portPath">OS port path that disappeared.</param>
    public LinkDisconnectedException(string portPath)
        : this(portPath, message: null)
    {
    }

    /// <summary>Creates the exception for a given port path with a specific OS detail.</summary>
    /// <param name="portPath">OS port path that disappeared.</param>
    /// <param name="message">Supplemental OS error detail surfaced to the UI.</param>
    public LinkDisconnectedException(string portPath, string? message)
        : base(message is null
            ? $"Serial device '{portPath}' was disconnected."
            : $"Serial device '{portPath}' was disconnected: {message}")
    {
        PortPath = portPath;
    }

    /// <summary>Creates the exception for a given port path wrapping an OS error.</summary>
    /// <param name="portPath">OS port path that disappeared.</param>
    /// <param name="innerException">The underlying OS error.</param>
    public LinkDisconnectedException(string portPath, Exception? innerException)
        : base($"Serial device '{portPath}' was disconnected.", innerException)
    {
        PortPath = portPath;
    }

    /// <summary>The port path that disappeared.</summary>
    public string PortPath { get; }
}
