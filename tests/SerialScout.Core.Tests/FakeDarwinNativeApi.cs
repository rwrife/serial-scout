using SerialScout.Core.Sessions.Posix;

namespace SerialScout.Core.Tests;

internal sealed class FakeDarwinNativeApi : FakeDarwinTermiosApi, IDarwinNativeApi
{
    internal int SerialDescriptor { get; set; } = 10;

    internal int LastError { get; set; }

    internal List<int> OpenFlags { get; } = [];

    internal List<int> PollTimeouts { get; } = [];

    internal List<int> ClosedDescriptors { get; } = [];

    internal List<int> ExclusiveDescriptors { get; } = [];

    internal Func<int, int>? SetExclusiveHandler { get; set; }

    internal Func<DarwinPollDescriptor[], int, int>? PollHandler { get; set; }

    internal Func<int, byte[], int, nint>? ReadHandler { get; set; }

    internal Func<int, byte[], int, int, nint>? WriteHandler { get; set; }

    public int Open(string path, int flags)
    {
        OpenFlags.Add(flags);
        return SerialDescriptor;
    }

    public int Close(int descriptor)
    {
        ClosedDescriptors.Add(descriptor);
        return 0;
    }

    public int SetExclusive(int descriptor)
    {
        ExclusiveDescriptors.Add(descriptor);
        return SetExclusiveHandler?.Invoke(descriptor) ?? 0;
    }

    public int Poll(DarwinPollDescriptor[] descriptors, int timeoutMilliseconds)
    {
        PollTimeouts.Add(timeoutMilliseconds);
        return PollHandler?.Invoke(descriptors, timeoutMilliseconds) ?? 0;
    }

    public nint Read(int descriptor, byte[] buffer, int count)
        => ReadHandler?.Invoke(descriptor, buffer, count) ?? 0;

    public nint Write(int descriptor, byte[] buffer, int offset, int count)
        => WriteHandler?.Invoke(descriptor, buffer, offset, count) ?? count;

    public int GetLastError() => LastError;
}
