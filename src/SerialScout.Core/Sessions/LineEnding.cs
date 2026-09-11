namespace SerialScout.Core.Sessions;

/// <summary>
/// Line ending appended to every text frame sent through a session, or suppressed
/// entirely for raw sends. Stored per profile so a device that needs CRLF never
/// silently receives LF.
/// </summary>
public enum LineEnding
{
    /// <summary>Append nothing; bytes are sent exactly as typed (raw mode).</summary>
    None,

    /// <summary>Append a single line feed (<c>\n</c>).</summary>
    LF,

    /// <summary>Append a single carriage return (<c>\r</c>).</summary>
    CR,

    /// <summary>Append carriage return followed by line feed (<c>\r\n</c>).</summary>
    CRLF,
}

/// <summary>
/// Mapping helpers between <see cref="LineEnding"/> and its wire bytes / stable
/// storage name. Names are persisted in the profile store and must never change.
/// </summary>
public static class LineEndings
{
    /// <summary>The bytes appended for an ending; empty for <see cref="LineEnding.None"/>.</summary>
    public static ReadOnlySpan<byte> ToBytes(this LineEnding ending) => ending switch
    {
        LineEnding.None => [],
        LineEnding.LF => new byte[] { (byte)'\n' },
        LineEnding.CR => new byte[] { (byte)'\r' },
        LineEnding.CRLF => new byte[] { (byte)'\r', (byte)'\n' },
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, "Unknown line ending."),
    };

    /// <summary>Stable lowercase name used for persistence and import/export.</summary>
    public static string ToStorageName(this LineEnding ending) => ending switch
    {
        LineEnding.None => "none",
        LineEnding.LF => "lf",
        LineEnding.CR => "cr",
        LineEnding.CRLF => "crlf",
        _ => throw new ArgumentOutOfRangeException(nameof(ending), ending, "Unknown line ending."),
    };

    /// <summary>
    /// Parses a persisted name case-insensitively. Throws <see cref="FormatException"/>
    /// for unknown names so corrupt rows surface loudly instead of guessing an ending.
    /// </summary>
    public static LineEnding ParseStorageName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.ToLowerInvariant() switch
        {
            "none" => LineEnding.None,
            "lf" => LineEnding.LF,
            "cr" => LineEnding.CR,
            "crlf" => LineEnding.CRLF,
            _ => throw new FormatException($"Unknown line ending name '{name}'."),
        };
    }
}
