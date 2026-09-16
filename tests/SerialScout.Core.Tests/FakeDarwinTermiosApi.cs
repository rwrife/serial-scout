using SerialScout.Core.Sessions.Posix;

namespace SerialScout.Core.Tests;

internal class FakeDarwinTermiosApi : IDarwinTermiosApi
{
    internal DarwinTermios InitialSettings { get; set; } = new()
    {
        ControlCharacters = new byte[DarwinConstants.ControlCharacterCount],
    };

    internal DarwinTermios AppliedSettings { get; private set; }

    internal int MakeRawCalls { get; private set; }

    internal int GetTerminalSettingsCalls { get; private set; }

    internal int ApplyAction { get; private set; } = -1;

    public int GetTerminalSettings(int descriptor, out DarwinTermios settings)
    {
        GetTerminalSettingsCalls++;
        settings = InitialSettings;
        settings.ControlCharacters = (byte[])InitialSettings.ControlCharacters.Clone();
        return 0;
    }

    public void MakeRaw(ref DarwinTermios settings)
    {
        MakeRawCalls++;
    }

    public int SetInputSpeed(ref DarwinTermios settings, ulong speed)
    {
        settings.InputSpeed = speed;
        return 0;
    }

    public int SetOutputSpeed(ref DarwinTermios settings, ulong speed)
    {
        settings.OutputSpeed = speed;
        return 0;
    }

    public int SetTerminalSettings(int descriptor, int action, ref DarwinTermios settings)
    {
        ApplyAction = action;
        var applied = settings;
        applied.ControlCharacters = (byte[])settings.ControlCharacters.Clone();
        AppliedSettings = applied;
        return 0;
    }
}
