using System.Globalization;
using System.Text;

namespace SerialScout.Core.Sessions;

/// <summary>
/// Pure, UI-framework-free rendering of <see cref="LogEvent"/> snapshots into
/// human-readable text lines for the session log panel (issue #5).
///
/// Design constraints driven by the accessibility baseline:
/// <list type="bullet">
/// <item>Direction is encoded as an <c>RX</c>/<c>TX</c> text prefix, never as color,
/// so screen readers and monochrome displays convey the same information.</item>
/// <item>Timestamps use a fixed invariant <c>HH:mm:ss.fff</c> format (UTC) so the
/// output is stable across locales and trivially assertable in tests.</item>
/// <item>Filtering is a case-insensitive substring test against the decoded payload
/// text only — timestamps and direction prefixes never satisfy a user's search.</item>
/// </list>
/// Payload bytes are decoded as UTF-8 with replacement, mirroring the note on
/// <see cref="LogEvent"/>: partial multi-byte sequences across chunk boundaries show
/// up as the replacement character instead of losing whole events.
/// </summary>
public static class SessionLogRenderer
{
    /// <summary>Direction prefix for bytes received from the device.</summary>
    public const string ReceivedPrefix = "RX";

    /// <summary>Direction prefix for bytes sent to the device.</summary>
    public const string SentPrefix = "TX";

    /// <summary>Timestamp format applied to every rendered line (invariant culture).</summary>
    public const string TimestampFormat = "HH:mm:ss.fff";

    private static readonly char[] LineTrim = ['\r', '\n'];

    /// <summary>
    /// Renders retained events (oldest first) as one multi-line display string.
    /// </summary>
    /// <param name="events">Snapshot of retained log events.</param>
    /// <param name="filter">
    /// Optional case-insensitive substring filter applied to the decoded payload text.
    /// Blank or whitespace-only filters disable filtering.
    /// </param>
    /// <returns>
    /// One line per matching event (<c>HH:mm:ss.fff RX|TX &lt;text&gt;</c>), joined with
    /// <c>\n</c>. Returns an empty string when no event matches. Trailing line breaks
    /// inside an event payload are trimmed so joined output never gains phantom blank
    /// lines; embedded newlines inside the payload are preserved.
    /// </returns>
    public static string Render(IReadOnlyList<LogEvent> events, string? filter = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        var useFilter = !string.IsNullOrWhiteSpace(filter);
        var builder = new StringBuilder();
        foreach (var logEvent in events)
        {
            ArgumentNullException.ThrowIfNull(logEvent);

            var text = Decode(logEvent.Payload).TrimEnd(LineTrim);
            if (useFilter && !text.Contains(filter!, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder
                .Append(logEvent.Utc.ToString(TimestampFormat, CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(logEvent.Direction == LogEventDirection.Received ? ReceivedPrefix : SentPrefix)
                .Append(' ')
                .Append(text);
        }

        return builder.ToString();
    }

    /// <summary>Decodes payload bytes as UTF-8 with the default replacement fallback.</summary>
    /// <param name="payload">Raw event payload.</param>
    public static string Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Encoding.UTF8.GetString(payload);
    }
}
