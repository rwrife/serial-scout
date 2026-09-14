using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SerialScout.Core.Sessions;

namespace SerialScout.Core.Privacy;

/// <summary>
/// Pure redaction transforms that return copied events and payloads while retaining
/// the original event count, order, directions, and timestamps.
/// </summary>
public static partial class LogRedactor
{
    /// <summary>
    /// Applies <paramref name="preset"/> without modifying captured events. Text is
    /// decoded across adjacent same-direction events so split UTF-8 and secrets remain
    /// intact. Preserved text keeps its original event ownership, while a replacement
    /// marker belongs to the event containing the first redacted character. Matched
    /// characters in later events are removed, so a retained event can have an empty
    /// redacted payload.
    /// </summary>
    public static IReadOnlyList<LogEvent> Apply(IReadOnlyList<LogEvent> events, RedactionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return [];
        }

        var transformed = new List<LogEvent>(events.Count);
        for (var index = 0; index < events.Count;)
        {
            var first = events[index] ?? throw new ArgumentException("Events cannot contain null entries.", nameof(events));
            if (preset == RedactionPreset.None)
            {
                transformed.Add(first with { Payload = first.Payload.ToArray() });
                index++;
                continue;
            }

            var direction = first.Direction;
            var runStart = index;
            while (index < events.Count)
            {
                var source = events[index] ?? throw new ArgumentException("Events cannot contain null entries.", nameof(events));
                if (source.Direction != direction)
                {
                    break;
                }

                index++;
            }

            var text = RedactText(Decode(events, runStart, index), preset);
            var payloads = Enumerable.Range(runStart, index - runStart).Select(_ => new StringBuilder()).ToArray();
            for (var characterIndex = 0; characterIndex < text.Value.Length; characterIndex++)
            {
                payloads[text.Owners[characterIndex] - runStart].Append(text.Value[characterIndex]);
            }

            for (var eventIndex = runStart; eventIndex < index; eventIndex++)
            {
                var source = events[eventIndex] ?? throw new ArgumentException("Events cannot contain null entries.", nameof(events));
                transformed.Add(source with { Payload = Encoding.UTF8.GetBytes(payloads[eventIndex - runStart].ToString()) });
            }
        }

        return transformed;
    }

    internal static string RedactText(string text, RedactionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RedactText(new AttributedText(text, new int[text.Length]), preset).Value;
    }

    private static AttributedText RedactText(AttributedText text, RedactionPreset preset)
    {
        if ((preset & RedactionPreset.Tokens) != 0)
        {
            text = Replace(text, BearerTokenRegex(), static match =>
                new Replacement("[REDACTED:TOKEN]", match.Groups["prefix"].Length));
            text = Replace(text, AssignedTokenRegex(), static match =>
                new Replacement("[REDACTED:TOKEN]", match.Groups["prefix"].Length));
            text = Replace(text, StandaloneTokenRegex(), static _ => new Replacement("[REDACTED:TOKEN]"));
            text = Replace(text, JwtRegex(), static _ => new Replacement("[REDACTED:TOKEN]"));
        }

        if ((preset & RedactionPreset.NetworkIdentifiers) != 0)
        {
            text = Replace(text, MacAddressRegex(), static _ => new Replacement("[REDACTED:MAC]"));
            text = Replace(text, Ipv4AddressRegex(), static _ => new Replacement("[REDACTED:IP]"));
            text = Replace(text, Ipv6CandidateRegex(), static match =>
                IPAddress.TryParse(match.Value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
                    ? new Replacement("[REDACTED:IP]")
                    : null);
        }

        if ((preset & RedactionPreset.FilePaths) != 0)
        {
            text = Replace(text, WindowsPathRegex(), static _ => new Replacement("[REDACTED:PATH]"));
            text = Replace(text, UnixPathRegex(), static _ => new Replacement("[REDACTED:PATH]"));
        }

        return text;
    }

    private static AttributedText Decode(IReadOnlyList<LogEvent> events, int start, int end)
    {
        var byteCount = 0;
        for (var index = start; index < end; index++)
        {
            var source = events[index] ?? throw new ArgumentException("Events cannot contain null entries.", nameof(events));
            byteCount = checked(byteCount + source.Payload.Length);
        }

        var bytes = new byte[byteCount];
        var byteOwners = new int[byteCount];
        var offset = 0;
        for (var index = start; index < end; index++)
        {
            var payload = events[index]!.Payload;
            payload.CopyTo(bytes, offset);
            Array.Fill(byteOwners, index, offset, payload.Length);
            offset += payload.Length;
        }

        var value = new StringBuilder();
        var owners = new List<int>();
        offset = 0;
        while (offset < bytes.Length)
        {
            var status = Rune.DecodeFromUtf8(bytes.AsSpan(offset), out var rune, out var bytesConsumed);
            if (status == OperationStatus.Done)
            {
                var decoded = rune.ToString();
                value.Append(decoded);
                owners.AddRange(Enumerable.Repeat(byteOwners[offset], decoded.Length));
                offset += bytesConsumed;
                continue;
            }

            value.Append(Rune.ReplacementChar.ToString());
            owners.Add(byteOwners[offset]);
            offset += status == OperationStatus.NeedMoreData
                ? bytes.Length - offset
                : Math.Max(bytesConsumed, 1);
        }

        return new AttributedText(value.ToString(), owners.ToArray());
    }

    private static AttributedText Replace(AttributedText source, Regex regex, Func<Match, Replacement?> replacement)
    {
        var value = new StringBuilder(source.Value.Length);
        var owners = new List<int>(source.Owners.Length);
        var cursor = 0;

        foreach (Match match in regex.Matches(source.Value))
        {
            AppendOriginal(cursor, match.Index - cursor);
            var replacementResult = replacement(match);
            if (replacementResult is null)
            {
                AppendOriginal(match.Index, match.Length);
            }
            else
            {
                AppendOriginal(match.Index, replacementResult.PreservedPrefixLength);
                value.Append(replacementResult.Value);
                owners.AddRange(Enumerable.Repeat(
                    source.Owners[match.Index + replacementResult.PreservedPrefixLength],
                    replacementResult.Value.Length));
            }

            cursor = match.Index + match.Length;
        }

        AppendOriginal(cursor, source.Value.Length - cursor);
        return new AttributedText(value.ToString(), owners.ToArray());

        void AppendOriginal(int originalStart, int length)
        {
            value.Append(source.Value, originalStart, length);
            for (var index = originalStart; index < originalStart + length; index++)
            {
                owners.Add(source.Owners[index]);
            }
        }
    }

    private sealed record AttributedText(string Value, int[] Owners);

    private sealed record Replacement(string Value, int PreservedPrefixLength = 0);

    [GeneratedRegex("(?<prefix>\\bBearer\\s+)[A-Za-z0-9._~+\\-/=]{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?<prefix>\\b(?:api[_-]?key|access[_-]?token|auth[_-]?token|secret)\\s*[:=]\\s*)[^\\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssignedTokenRegex();

    [GeneratedRegex("\\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{12,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{12,})\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneTokenRegex();

    [GeneratedRegex("\\b[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex("(?<![0-9])(?:(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})\\.){3}(?:25[0-5]|2[0-4][0-9]|1?[0-9]{1,2})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4AddressRegex();

    [GeneratedRegex("(?<![0-9A-Fa-f:])[0-9A-Fa-f:]*:[0-9A-Fa-f:.]*(?![0-9A-Fa-f:])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6CandidateRegex();

    [GeneratedRegex("(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex("(?<![A-Za-z0-9])(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\r\\n\\t <>\"]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("(?<![A-Za-z0-9])/(?:[^/\\s<>\"]+/)*[^/\\s<>\"]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();
}
