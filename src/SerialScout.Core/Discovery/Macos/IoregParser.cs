using System.Globalization;
using System.Text.RegularExpressions;

namespace SerialScout.Core.Discovery.Macos;

/// <summary>
/// Pure parser for a narrow, well-defined slice of <c>ioreg -l -w 0</c> output.
/// It walks the nested object tree, tracks the IOKit ancestry of every
/// <c>IOSerialBSDClient</c> node, and lifts USB identity keys (<c>idVendor</c>,
/// <c>idProduct</c>, <c>USB Vendor Name</c>, <c>USB Product Name</c>, <c>iSerialNumber</c>)
/// from the nearest USB-ish ancestor. Decimal ids are converted to <c>0x</c>-prefixed hex.
/// Anything the output does not state stays <c>null</c> — the parser never guesses.
/// </summary>
public static class IoregParser
{
    private static readonly Regex ObjectHeader = new(
        @"^\s*\+-o\s+(?<name>.+?)\s+<class\s+(?<cls>\w+),",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PropertyLine = new(
        @"^\s*""(?<key>[^""]+)""\s*=\s*(?<value>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses raw ioreg output into serial client entries in document order.</summary>
    public static IReadOnlyList<MacosSerialEntry> Parse(string ioregOutput)
    {
        ArgumentNullException.ThrowIfNull(ioregOutput);

        var results = new List<MacosSerialEntry>();
        var stack = new Stack<Frame>();

        foreach (var line in ioregOutput.Split('\n'))
        {
            var header = ObjectHeader.Match(line);
            if (header.Success)
            {
                stack.Push(new Frame(header.Groups["cls"].Value));
                continue;
            }

            if (stack.Count == 0)
            {
                continue;
            }

            var top = stack.Peek();

            var trimmedLine = line.Trim();
            if (trimmedLine.Length == 1 && (trimmedLine[0] == '{' || trimmedLine[0] == '}'))
            {
                if (trimmedLine[0] == '{')
                {
                    if (!top.PropertiesOpen)
                    {
                        top.PropertiesOpen = true;
                    }
                    else
                    {
                        top.NestedDepth++;
                    }
                }
                else if (top.NestedDepth > 0)
                {
                    top.NestedDepth--;
                }
                else if (top.PropertiesOpen)
                {
                    top.PropertiesOpen = false;
                    var closing = stack.Pop();
                    if (IsSerialClient(closing.ClassName))
                    {
                        results.Add(BuildEntry(closing, stack));
                    }
                }

                continue;
            }

            if (trimmedLine == ")")
            {
                if (top.NestedDepth > 0)
                {
                    top.NestedDepth--;
                }

                continue;
            }

            if (!top.PropertiesOpen || top.NestedDepth > 0)
            {
                continue;
            }

            var prop = PropertyLine.Match(line);
            if (prop.Success)
            {
                var value = prop.Groups["value"].Value;

                // Multi-line nested blocks ("key" = { ... } or "key" = ( ... )): skip their
                // contents so nested keys never leak into this object's property map.
                if (value is "{" or "(")
                {
                    top.NestedDepth++;
                    continue;
                }

                top.Properties[prop.Groups["key"].Value] = value;
            }
        }

        return results;
    }

    /// <summary>Formats a decimal IOKit id as <c>0x</c>-prefixed hex, or <c>null</c> when unparseable.</summary>
    public static string? FormatUsbId(string? decimalText)
    {
        var text = decimalText?.Trim().Trim('"');
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value <= 0xFFFF)
        {
            return string.Format(CultureInfo.InvariantCulture, "0x{0:X4}", value);
        }

        return null;
    }

    private static bool IsSerialClient(string className) =>
        className.Contains("SerialBSDClient", StringComparison.Ordinal);

    private static MacosSerialEntry BuildEntry(Frame serialFrame, Stack<Frame> ancestors)
    {
        string? vendorId = null;
        string? productId = null;
        string? manufacturer = null;
        string? product = null;
        string? serialNumber = null;
        string? identityClass = null;

        // Walk ancestry innermost-first (closest IOKit ancestor wins), falling back to the
        // serial client's own properties last.
        var chain = ancestors.Append(serialFrame).ToList();
        foreach (var frame in chain)
        {
            vendorId ??= FormatUsbId(frame.Properties.GetValueOrDefault("idVendor"));
            productId ??= FormatUsbId(frame.Properties.GetValueOrDefault("idProduct"));
            manufacturer ??= Unquote(frame.Properties.GetValueOrDefault("USB Vendor Name"));
            product ??= Unquote(frame.Properties.GetValueOrDefault("USB Product Name"));
            serialNumber ??= Unquote(frame.Properties.GetValueOrDefault("iSerialNumber"));

            if (identityClass is null && HasAnyUsbIdentityKey(frame) && !ReferenceEquals(frame, serialFrame))
            {
                identityClass = frame.ClassName;
            }
        }

        var callout = Unquote(serialFrame.Properties.GetValueOrDefault("IOCalloutDevice"));
        var tty = Unquote(serialFrame.Properties.GetValueOrDefault("IOTTYDevice"));

        return new MacosSerialEntry(
            PortPath: callout ?? tty ?? string.Empty,
            TTYDevice: tty,
            VendorIdHex: vendorId,
            ProductIdHex: productId,
            Manufacturer: manufacturer,
            Product: product,
            SerialNumber: serialNumber,
            ClassName: identityClass ?? serialFrame.ClassName);
    }

    private static bool HasAnyUsbIdentityKey(Frame frame) =>
        frame.Properties.ContainsKey("idVendor")
        || frame.Properties.ContainsKey("idProduct")
        || frame.Properties.ContainsKey("USB Vendor Name")
        || frame.Properties.ContainsKey("USB Product Name")
        || frame.Properties.ContainsKey("iSerialNumber");

    private static string? Unquote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        return text.Length == 0 ? null : text;
    }

    private sealed class Frame(string className)
    {
        public string ClassName { get; } = className;

        public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

        public bool PropertiesOpen { get; set; }

        public int NestedDepth { get; set; }
    }
}
