using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SerialScout.Core.Discovery.Macos;

/// <summary>
/// Production <see cref="IMacosPortSource"/>: parses a full <c>ioreg -l -w 0</c> dump and
/// probes each callout device with a lock-only, non-blocking open via libc.
/// </summary>
/// <param name="probeAvailability">
/// When true, each port gets an advisory-lock probe to distinguish ready/busy/denied.
/// Callers that must stay strictly hands-off can disable probing and accept
/// <see cref="ScanState.Unknown"/> states.
/// </param>
public sealed class IoregMacosPortSource(bool probeAvailability = true) : IMacosPortSource
{
    static IoregMacosPortSource()
    {
        NativeLibrary.SetDllImportResolver(typeof(IoregMacosPortSource).Assembly, ResolveLibrary);
    }

    /// <inheritdoc />
    public bool IsSupportedPlatform => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    public IReadOnlyList<(MacosSerialEntry Entry, MacosProbeResult Probe)> ReadEntries()
    {
        if (!IsSupportedPlatform)
        {
            return Array.Empty<(MacosSerialEntry, MacosProbeResult)>();
        }

        var output = RunIoreg();
        var results = new List<(MacosSerialEntry, MacosProbeResult)>();

        foreach (var entry in IoregParser.Parse(output))
        {
            var probe = probeAvailability && entry.PortPath.Length > 0
                ? ProbePort(entry.PortPath)
                : MacosProbeResult.NotProbed;
            results.Add((entry, probe));
        }

        return results;
    }

    private static string RunIoreg()
    {
        var startInfo = new ProcessStartInfo("ioreg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("0");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ioreg.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "ioreg exited with code {0}: {1}",
                process.ExitCode,
                error));
        }

        return output;
    }

    private static MacosProbeResult ProbePort(string path)
    {
        // O_RDONLY (0) | O_NONBLOCK (0x0004) | O_EXLOCK (0x0080) — Darwin fcntl flags.
        // The open takes the advisory lock BSD terminals use and then immediately closes;
        // nothing is read or written and no terminal settings are touched.
        var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
        int fd;
        try
        {
            fd = Native.open(pathPtr, OReadLockNonBlocking);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }

        if (fd >= 0)
        {
            _ = Native.close(fd);
            return MacosProbeResult.Available;
        }

        // Capture errno before close() can overwrite it.
        var probeError = Marshal.GetLastPInvokeError();
        _ = Native.close(fd);
        return MacosPortMapper.MapProbeError(probeError);
    }

    private const int OReadLockNonBlocking = 0x0000 | 0x0004 | 0x0080;

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibcName)
        {
            return IntPtr.Zero;
        }

        // The "c" pseudo-name only resolves on Apple platforms; load the real libc so the
        // type also works when unit-tested on Linux CI hosts.
        string[] candidates = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
            ? ["libc.dylib", "libc", LibcName]
            : ["libc.so.6", "libc.so", LibcName];

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private const string LibcName = "c";

    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(LibcName, CallingConvention = CallingConvention.Cdecl, SetLastError = true, ExactSpelling = true)]
        public static extern int open(IntPtr path, int flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(LibcName, CallingConvention = CallingConvention.Cdecl, SetLastError = true, ExactSpelling = true)]
        public static extern int close(int fd);
    }
}
