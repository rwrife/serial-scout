using System.Text;

namespace SerialScout.Core.Sessions;

/// <summary>
/// A user-defined quick-send entry (e.g. an AT command preset). Entries are stored as
/// ordered name/text pairs in the local profile store; text may contain the literal
/// escape sequences <c>\r</c>, <c>\n</c>, <c>\t</c>, and <c>\\</c> which
/// <see cref="Unescape"/> expands at send time, so multi-line or CR-only macros stay
/// expressible without multiline storage strings.
/// </summary>
/// <param name="Name">Stable unique name of the preset.</param>
/// <param name="Text">Raw preset text with optional backslash escapes.</param>
public sealed record SendPreset(string Name, string Text)
{
    /// <summary>UTF-8 bytes of the unescaped preset text.</summary>
    public byte[] Expand() => Unescape(Text);

    /// <summary>
    /// Expands the literal escape sequences <c>\r</c>, <c>\n</c>, <c>\t</c>, and
    /// <c>\\</c>. An unrecognized or trailing backslash is passed through verbatim so
    /// presets that legitimately contain backslashes never lose data.
    /// </summary>
    public static byte[] Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i == text.Length - 1)
            {
                AppendUtf8(bytes, text[i]);
                continue;
            }

            char next = text[i + 1];
            switch (next)
            {
                case 'r':
                    bytes.Add((byte)'\r');
                    i++;
                    break;
                case 'n':
                    bytes.Add((byte)'\n');
                    i++;
                    break;
                case 't':
                    bytes.Add((byte)'\t');
                    i++;
                    break;
                case '\\':
                    bytes.Add((byte)'\\');
                    i++;
                    break;
                default:
                    AppendUtf8(bytes, text[i]);
                    break;
            }
        }

        return bytes.ToArray();
    }

    private static void AppendUtf8(List<byte> bytes, char c)
    {
        bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
    }
}

/// <summary>
/// Most-recently-sent commands with optional per-port scoping. The engine records the
/// exact framed payload it transmitted, so history replays reproduce wire behavior.
/// Insertion is O(capacity); eviction removes the oldest entry.
/// </summary>
public sealed class SendHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<string>> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;

    /// <summary>Creates history keeping at most <paramref name="capacity"/> entries per scope.</summary>
    /// <param name="capacity">Entries retained per scope; must be positive.</param>
    public SendHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>
    /// Records one sent payload string for a port scope (case-insensitive port path).
    /// Consecutive duplicates are coalesced so re-sending the same command does not
    /// push useful entries out of the list.
    /// </summary>
    public void Record(string scope, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            if (!_scopes.TryGetValue(scope, out var list))
            {
                list = new LinkedList<string>();
                _scopes[scope] = list;
            }

            if (list.Last is not null && list.Last.Value == payload)
            {
                return;
            }

            list.AddLast(payload);
            while (list.Count > _capacity)
            {
                list.RemoveFirst();
            }
        }
    }

    /// <summary>Entries for a scope, oldest first; empty when the scope is unknown.</summary>
    public IReadOnlyList<string> Entries(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_gate)
        {
            return _scopes.TryGetValue(scope, out var list) ? list.ToArray() : Array.Empty<string>();
        }
    }

    /// <summary>Drops all entries for one scope.</summary>
    public void Clear(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_gate)
        {
            _ = _scopes.Remove(scope);
        }
    }
}
