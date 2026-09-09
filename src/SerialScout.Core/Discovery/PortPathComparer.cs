namespace SerialScout.Core.Discovery;

/// <summary>
/// Natural, deterministic ordering for OS port paths: alphabetic segments compare
/// case-insensitively while digit runs compare numerically, so <c>COM2</c> sorts before
/// <c>COM10</c> and <c>/dev/cu.usbserial-9</c> sorts before <c>/dev/cu.usbserial-110</c>.
/// Equal-under-natural-comparison paths fall back to ordinal order for a stable total order.
/// </summary>
public sealed class PortPathComparer : IComparer<string?>
{
    /// <summary>Shared instance.</summary>
    public static readonly PortPathComparer Natural = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var ix = 0;
        var iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            var cx = x[ix];
            var cy = y[iy];
            var dx = char.IsAsciiDigit(cx);
            var dy = char.IsAsciiDigit(cy);

            if (dx && dy)
            {
                var digitCompare = CompareDigitRun(x.AsSpan(ix), y.AsSpan(iy), out var advanceX, out var advanceY);
                if (digitCompare != 0)
                {
                    return digitCompare;
                }

                ix += advanceX;
                iy += advanceY;
                continue;
            }

            if (dx != dy)
            {
                // A digit run sorts before an alphabetic run at the same position;
                // deterministic tie-break between the two shapes.
                return dx ? -1 : 1;
            }

            var charCompare = char.ToUpperInvariant(cx) - char.ToUpperInvariant(cy);
            if (charCompare != 0)
            {
                return charCompare < 0 ? -1 : 1;
            }

            ix++;
            iy++;
        }

        var lengthCompare = (x.Length - ix).CompareTo(y.Length - iy);
        return lengthCompare != 0
            ? lengthCompare
            : string.CompareOrdinal(x, y);
    }

    private static int CompareDigitRun(ReadOnlySpan<char> a, ReadOnlySpan<char> b, out int advanceA, out int advanceB)
    {
        var runA = RunLength(a);
        var runB = RunLength(b);
        advanceA = runA;
        advanceB = runB;

        var trimmedA = a[..runA].TrimStart('0');
        var trimmedB = b[..runB].TrimStart('0');

        if (trimmedA.Length != trimmedB.Length)
        {
            return trimmedA.Length < trimmedB.Length ? -1 : 1;
        }

        for (var i = 0; i < trimmedA.Length; i++)
        {
            if (trimmedA[i] != trimmedB[i])
            {
                return trimmedA[i] < trimmedB[i] ? -1 : 1;
            }
        }

        // Same numeric value; longer literal run (more leading zeros) sorts later.
        return runA == runB ? 0 : (runA < runB ? -1 : 1);
    }

    private static int RunLength(ReadOnlySpan<char> value)
    {
        var length = 0;
        while (length < value.Length && char.IsAsciiDigit(value[length]))
        {
            length++;
        }

        return length;
    }
}
