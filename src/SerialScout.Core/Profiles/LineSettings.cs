namespace SerialScout.Core.Profiles;

/// <summary>
/// Default serial line settings stored with a device profile. Values are validated at
/// construction so a persisted profile can never carry a line configuration the session
/// engine would have to guess about later.
/// </summary>
public sealed record LineSettings
{
    /// <summary>
    /// Creates validated line settings.
    /// </summary>
    /// <param name="baudRate">Bits per second; must be positive.</param>
    /// <param name="dataBits">Data bits per frame; must be between 5 and 8 inclusive.</param>
    /// <param name="parity">Line parity.</param>
    /// <param name="stopBits">Stop bits.</param>
    public LineSettings(
        int baudRate = DefaultBaudRate,
        int dataBits = 8,
        LineParity parity = LineParity.None,
        LineStopBits stopBits = LineStopBits.One)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baudRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(dataBits, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataBits, 8);

        BaudRate = baudRate;
        DataBits = dataBits;
        Parity = parity;
        StopBits = stopBits;
    }

    /// <summary>Most common modern default used when a profile does not specify line settings.</summary>
    public const int DefaultBaudRate = 115200;

    /// <summary>Bits per second.</summary>
    public int BaudRate { get; }

    /// <summary>Data bits per frame (5..8).</summary>
    public int DataBits { get; }

    /// <summary>Line parity.</summary>
    public LineParity Parity { get; }

    /// <summary>Stop bits.</summary>
    public LineStopBits StopBits { get; }

    /// <summary>The shared default instance: 115200 8N1.</summary>
    public static LineSettings Default { get; } = new();
}

/// <summary>
/// Line parity options a profile can pin.
/// </summary>
public enum LineParity
{
    /// <summary>No parity bit.</summary>
    None,

    /// <summary>Odd parity.</summary>
    Odd,

    /// <summary>Even parity.</summary>
    Even,
}

/// <summary>
/// Stop-bit options a profile can pin.
/// </summary>
public enum LineStopBits
{
    /// <summary>One stop bit.</summary>
    One,

    /// <summary>Two stop bits.</summary>
    Two,
}
