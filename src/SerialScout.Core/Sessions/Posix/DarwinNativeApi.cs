using System.Runtime.InteropServices;

namespace SerialScout.Core.Sessions.Posix;

internal sealed class DarwinNativeApi : IDarwinNativeApi
{
    private const string LibSystem = "libSystem.B.dylib";
    public int Open(string path, int flags)
    {
        var nativePath = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            return NativeOpen(nativePath, flags);
        }
        finally
        {
            Marshal.FreeCoTaskMem(nativePath);
        }
    }

    public int Close(int descriptor) => NativeClose(descriptor);

    public int SetExclusive(int descriptor)
        => NativeSetExclusive(descriptor, 0x2000740dUL);

    public int Poll(DarwinPollDescriptor[] descriptors, int timeoutMilliseconds)
        => NativePoll(descriptors, (uint)descriptors.Length, timeoutMilliseconds);

    public nint Read(int descriptor, byte[] buffer, int count)
        => NativeRead(descriptor, buffer, checked((nuint)count));

    public nint Write(int descriptor, byte[] buffer, int offset, int count)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var pointer = IntPtr.Add(handle.AddrOfPinnedObject(), offset);
            return NativeWrite(descriptor, pointer, checked((nuint)count));
        }
        finally
        {
            handle.Free();
        }
    }

    public int GetLastError() => Marshal.GetLastPInvokeError();

    public int GetTerminalSettings(int descriptor, out DarwinTermios settings)
        => NativeGetTerminalSettings(descriptor, out settings);

    public void MakeRaw(ref DarwinTermios settings) => NativeMakeRaw(ref settings);

    public int SetInputSpeed(ref DarwinTermios settings, ulong speed)
        => NativeSetInputSpeed(ref settings, speed);

    public int SetOutputSpeed(ref DarwinTermios settings, ulong speed)
        => NativeSetOutputSpeed(ref settings, speed);

    public int SetTerminalSettings(int descriptor, int action, ref DarwinTermios settings)
        => NativeSetTerminalSettings(descriptor, action, ref settings);

    [DllImport(LibSystem, EntryPoint = "open", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int NativeOpen(IntPtr path, int flags);

    [DllImport(LibSystem, EntryPoint = "close", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int NativeClose(int descriptor);

    [DllImport(LibSystem, EntryPoint = "ioctl", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int NativeSetExclusive(int descriptor, ulong request);

    [DllImport(LibSystem, EntryPoint = "poll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int NativePoll(
        [In, Out] DarwinPollDescriptor[] descriptors,
        uint descriptorCount,
        int timeoutMilliseconds);

    [DllImport(LibSystem, EntryPoint = "read", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern nint NativeRead(int descriptor, [Out] byte[] buffer, nuint count);

    [DllImport(LibSystem, EntryPoint = "write", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern nint NativeWrite(int descriptor, IntPtr buffer, nuint count);

    [DllImport(LibSystem, EntryPoint = "tcgetattr", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int NativeGetTerminalSettings(int descriptor, out DarwinTermios settings);

    [DllImport(LibSystem, EntryPoint = "cfmakeraw", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeMakeRaw(ref DarwinTermios settings);

    [DllImport(LibSystem, EntryPoint = "cfsetispeed", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int NativeSetInputSpeed(ref DarwinTermios settings, ulong speed);

    [DllImport(LibSystem, EntryPoint = "cfsetospeed", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int NativeSetOutputSpeed(ref DarwinTermios settings, ulong speed);

    [DllImport(LibSystem, EntryPoint = "tcsetattr", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int NativeSetTerminalSettings(
        int descriptor,
        int action,
        ref DarwinTermios settings);
}
