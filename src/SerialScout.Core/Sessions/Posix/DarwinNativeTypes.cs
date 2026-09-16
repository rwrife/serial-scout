using System.Runtime.InteropServices;

namespace SerialScout.Core.Sessions.Posix;

internal static class DarwinConstants
{
    internal const ulong InputParityCheck = 0x00000010;

    internal const ulong ControlSize = 0x00000300;
    internal const ulong CharacterSize5 = 0x00000000;
    internal const ulong CharacterSize6 = 0x00000100;
    internal const ulong CharacterSize7 = 0x00000200;
    internal const ulong CharacterSize8 = 0x00000300;
    internal const ulong TwoStopBits = 0x00000400;
    internal const ulong EnableReceiver = 0x00000800;
    internal const ulong ParityEnable = 0x00001000;
    internal const ulong OddParity = 0x00002000;
    internal const ulong HangUpOnClose = 0x00004000;
    internal const ulong LocalMode = 0x00008000;
    internal const ulong ClearToSendOutputFlow = 0x00010000;
    internal const ulong RequestToSendInputFlow = 0x00020000;
    internal const ulong DataTerminalReadyInputFlow = 0x00040000;
    internal const ulong DataSetReadyOutputFlow = 0x00080000;
    internal const ulong CarrierDetectOutputFlow = 0x00100000;
    internal const ulong HardwareFlowControl = ClearToSendOutputFlow | RequestToSendInputFlow |
        DataTerminalReadyInputFlow | DataSetReadyOutputFlow | CarrierDetectOutputFlow;

    internal const int MinimumBytes = 16;
    internal const int ReadTimeout = 17;
    internal const int ControlCharacterCount = 20;
    internal const int ApplyNow = 0;

    internal const int Readable = 0x0001;
    internal const int Writable = 0x0004;
    internal const int PollError = 0x0008;
    internal const int PollHangUp = 0x0010;
    internal const int PollInvalid = 0x0020;

    internal const int Interrupted = 4;
    internal const int IoError = 5;
    internal const int NoDeviceOrAddress = 6;
    internal const int BadDescriptor = 9;
    internal const int NoDevice = 19;
    internal const int TryAgain = 35;

    internal const int OpenReadWrite = 0x0002;

    internal const int OpenNonBlocking = 0x0004;
    internal const int OpenNoControllingTerminal = 0x00020000;
    internal const int OpenCloseOnExec = 0x01000000;
    internal const int SerialOpenFlags =
        OpenReadWrite | OpenNonBlocking | OpenNoControllingTerminal | OpenCloseOnExec;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DarwinPollDescriptor
{
    internal int Descriptor;
    internal short Events;
    internal short ReturnedEvents;
}



internal interface IDarwinNativeApi : IDarwinTermiosApi
{
    int Open(string path, int flags);

    int Close(int descriptor);

    int SetExclusive(int descriptor);

    int Poll(DarwinPollDescriptor[] descriptors, int timeoutMilliseconds);

    nint Read(int descriptor, byte[] buffer, int count);

    nint Write(int descriptor, byte[] buffer, int offset, int count);

    int GetLastError();
}
[StructLayout(LayoutKind.Sequential)]
internal struct DarwinTermios
{
    internal ulong InputFlags;
    internal ulong OutputFlags;
    internal ulong ControlFlags;
    internal ulong LocalFlags;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = DarwinConstants.ControlCharacterCount)]
    internal byte[] ControlCharacters;

    internal ulong InputSpeed;
    internal ulong OutputSpeed;
}

internal interface IDarwinTermiosApi
{
    int GetTerminalSettings(int descriptor, out DarwinTermios settings);

    void MakeRaw(ref DarwinTermios settings);

    int SetInputSpeed(ref DarwinTermios settings, ulong speed);

    int SetOutputSpeed(ref DarwinTermios settings, ulong speed);

    int SetTerminalSettings(int descriptor, int action, ref DarwinTermios settings);
}
