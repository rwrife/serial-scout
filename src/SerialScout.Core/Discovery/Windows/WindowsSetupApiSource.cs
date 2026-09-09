using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SerialScout.Core.Discovery.Windows;

/// <summary>
/// Production <see cref="IWindowsPnpRowSource"/> backed by SetupAPI. Enumerates
/// Ports-class devices (class guid <c>{4d36e978-e325-11ce-bfc1-08002be10318}</c>) and probes
/// each port's availability with a zero-access <c>CreateFile</c> query open.
/// </summary>
/// <param name="probeAvailability">
/// When true, each enumerated port gets a query open to distinguish ready/busy. Some
/// USB-serial drivers react to any open attempt, so callers that must stay strictly
/// hands-off can disable probing and accept <see cref="ScanState.Unknown"/> states.
/// </param>
public sealed class WindowsSetupApiSource(bool probeAvailability = true) : IWindowsPnpRowSource
{
    /// <inheritdoc />
    public bool IsSupportedPlatform => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public IReadOnlyList<WindowsPnpRow> ReadRows()
    {
        if (!IsSupportedPlatform)
        {
            return Array.Empty<WindowsPnpRow>();
        }

        var rows = new List<WindowsPnpRow>();
        var classGuid = PortsClassGuid;
        var set = Native.SetupDiGetClassDevsW(
            ref classGuid,
            deviceEnumerator: null,
            hwndParent: IntPtr.Zero,
            flags: DigcfPresent);

        if (set is null || set.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetClassDevs failed.");
        }

        using (set)
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDevinfoData();
                data.CbSize = Marshal.SizeOf<SpDevinfoData>();
                if (!Native.SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    break; // ERROR_NO_MORE_ITEMS or unexpected end of enumeration.
                }

                var friendlyName = ReadStringProperty(set, ref data, SpdrFriendlyName);
                var description = ReadStringProperty(set, ref data, SpdrDeviceDesc);
                if (WindowsPnpParser.ExtractComPort(friendlyName) is null
                    && WindowsPnpParser.ExtractComPort(description) is null)
                {
                    continue; // Not addressable as a COM port (e.g. Bluetooth enumerators).
                }

                var hardwareIds = ReadMultiSzProperty(set, ref data, SpdrHardwareId);
                var instanceId = ReadInstanceId(set, ref data);
                var probe = probeAvailability
                    ? ProbePort(WindowsPnpParser.ExtractComPort(friendlyName) ?? WindowsPnpParser.ExtractComPort(description)!)
                    : WindowsProbeResult.NotProbed;

                rows.Add(new WindowsPnpRow(
                    InstanceId: instanceId ?? string.Empty,
                    FriendlyName: friendlyName,
                    DeviceDescription: description,
                    HardwareIds: hardwareIds,
                    Probe: probe));
            }
        }

        return rows;
    }

    private static WindowsProbeResult ProbePort(string portPath)
    {
        var handle = Native.CreateFileW(
            @"\\.\" + portPath,
            desiredAccess: 0, // Query-only open: no reads/writes, no settings changes.
            shareMode: FileShare.ReadWrite,
            securityAttributes: IntPtr.Zero,
            creationDisposition: OpenExisting,
            flagsAndAttributes: 0,
            templateFile: IntPtr.Zero);

        try
        {
            if (!handle.IsInvalid)
            {
                return WindowsProbeResult.Available;
            }

            // Capture the error before any other call can overwrite it.
            var win32Error = Marshal.GetLastPInvokeError();
            return WindowsPnpParser.MapProbeError(win32Error);
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static string? ReadStringProperty(SafeDeviceInfoSet set, ref SpDevinfoData data, uint property)
    {
        var buffer = new byte[BufferSize];
        if (!Native.SetupDiGetDeviceRegistryPropertyW(
                set, ref data, property, out _, buffer, (uint)buffer.Length, out var requiredSize)
            || requiredSize <= 2)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer, 0, (int)requiredSize);
        var nul = text.IndexOf('\0');
        if (nul >= 0)
        {
            text = text[..nul];
        }

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string[] ReadMultiSzProperty(SafeDeviceInfoSet set, ref SpDevinfoData data, uint property)
    {
        var buffer = new byte[BufferSize];
        if (!Native.SetupDiGetDeviceRegistryPropertyW(
                set, ref data, property, out _, buffer, (uint)buffer.Length, out var requiredSize)
            || requiredSize <= 2)
        {
            return Array.Empty<string>();
        }

        var text = Encoding.Unicode.GetString(buffer, 0, (int)requiredSize);
        return text
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.Length > 0)
            .ToArray();
    }

    private static string? ReadInstanceId(SafeDeviceInfoSet set, ref SpDevinfoData data)
    {
        var buffer = new char[BufferSize / 2];
        if (!Native.SetupDiGetDeviceInstanceIdW(set, ref data, buffer, (uint)buffer.Length, out var requiredSize)
            || requiredSize <= 1)
        {
            return null;
        }

        var text = new string(buffer, 0, (int)requiredSize - 1);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
    private static Guid PortsClassGuid => new("4d36e978-e325-11ce-bfc1-08002be10318");

    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrDeviceDesc = 0x00000000;
    private const uint SpdrHardwareId = 0x00000001;
    private const uint SpdrFriendlyName = 0x0000000C; // SPDR_FRIENDLYNAME (setupapi.h).
    private const int BufferSize = 4096;
    private const int ErrorInsufficientBuffer = 122;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public nuint DeviceInst;
        public nint Reserved;
    }

    private static class Native
    {
        [DllImport(Lib.SetupApi, ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeDeviceInfoSet SetupDiGetClassDevsW(
            ref Guid classGuid,
            string? deviceEnumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport(Lib.SetupApi, ExactSpelling = true, SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(
            SafeDeviceInfoSet deviceInfoSet,
            uint memberIndex,
            ref SpDevinfoData deviceInfoData);

        [DllImport(Lib.SetupApi, ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryPropertyW(
            SafeDeviceInfoSet deviceInfoSet,
            ref SpDevinfoData deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize);

        [DllImport(Lib.SetupApi, ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInstanceIdW(
            SafeDeviceInfoSet deviceInfoSet,
            ref SpDevinfoData deviceInfoData,
            char[] deviceInstanceIdBuffer,
            uint deviceInstanceIdBufferSize,
            out uint requiredSize);

        [DllImport(Lib.Kernel32, ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }

    private static class Lib
    {
        public const string SetupApi = "setupapi.dll";
        public const string Kernel32 = "kernel32.dll";
    }
}

/// <summary>Safe handle for a SetupAPI device information set.</summary>
internal sealed class SafeDeviceInfoSet : SafeHandle
{
    public SafeDeviceInfoSet()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero || handle == InvalidHandleValue;

    protected override bool ReleaseHandle() => Native.SetupDiDestroyDeviceInfoList(handle);

    private const nint InvalidHandleValue = -1;

    private static class Native
    {
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }
}
