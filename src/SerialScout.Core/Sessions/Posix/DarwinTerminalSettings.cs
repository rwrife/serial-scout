using SerialScout.Core.Profiles;

namespace SerialScout.Core.Sessions.Posix;

internal static class DarwinTerminalSettings
{
    private static readonly HashSet<int> SupportedBaudRates =
    [
        50, 75, 110, 134, 150, 200, 300, 600, 1200, 1800, 2400, 4800,
        7200, 9600, 14400, 19200, 28800, 38400, 57600, 76800, 115200, 230400,
    ];

    internal static void Validate(LineSettings lineSettings)
    {
        ArgumentNullException.ThrowIfNull(lineSettings);

        if (!SupportedBaudRates.Contains(lineSettings.BaudRate))
        {
            throw new IOException(
                $"Baud rate {lineSettings.BaudRate} is not supported by Darwin termios.");
        }

        if (!Enum.IsDefined(lineSettings.Parity))
        {
            throw new ArgumentOutOfRangeException(nameof(lineSettings), lineSettings.Parity, "Unknown parity.");
        }

        if (!Enum.IsDefined(lineSettings.StopBits))
        {
            throw new ArgumentOutOfRangeException(nameof(lineSettings), lineSettings.StopBits, "Unknown stop-bit setting.");
        }
    }

    internal static void Configure(IDarwinTermiosApi api, int descriptor, LineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(api);
        Validate(settings);

        if (api.GetTerminalSettings(descriptor, out var terminal) != 0)
        {
            throw new IOException("tcgetattr failed.");
        }

        api.MakeRaw(ref terminal);
        terminal.ControlCharacters ??= new byte[DarwinConstants.ControlCharacterCount];
        terminal.ControlCharacters[DarwinConstants.MinimumBytes] = 0;
        terminal.ControlCharacters[DarwinConstants.ReadTimeout] = 0;

        terminal.ControlFlags &= ~(
            DarwinConstants.ControlSize |
            DarwinConstants.TwoStopBits |
            DarwinConstants.ParityEnable |
            DarwinConstants.OddParity |
            DarwinConstants.HardwareFlowControl |
            DarwinConstants.HangUpOnClose);
        terminal.ControlFlags |= DarwinConstants.LocalMode | DarwinConstants.EnableReceiver;
        terminal.ControlFlags |= settings.DataBits switch
        {
            5 => DarwinConstants.CharacterSize5,
            6 => DarwinConstants.CharacterSize6,
            7 => DarwinConstants.CharacterSize7,
            8 => DarwinConstants.CharacterSize8,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.DataBits, "Unknown data-bit setting."),
        };

        if (settings.StopBits == LineStopBits.Two)
        {
            terminal.ControlFlags |= DarwinConstants.TwoStopBits;
        }

        terminal.InputFlags &= ~DarwinConstants.InputParityCheck;
        terminal.ControlFlags |= settings.Parity switch
        {
            LineParity.None => 0,
            LineParity.Even => DarwinConstants.ParityEnable,
            LineParity.Odd => DarwinConstants.ParityEnable | DarwinConstants.OddParity,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Parity, "Unknown parity."),
        };
        if (settings.Parity != LineParity.None)
        {
            terminal.InputFlags |= DarwinConstants.InputParityCheck;
        }

        var speed = checked((ulong)settings.BaudRate);
        if (api.SetInputSpeed(ref terminal, speed) != 0 ||
            api.SetOutputSpeed(ref terminal, speed) != 0)
        {
            throw new IOException("cfsetispeed/cfsetospeed failed.");
        }

        if (api.SetTerminalSettings(descriptor, DarwinConstants.ApplyNow, ref terminal) != 0)
        {
            throw new IOException("tcsetattr failed.");
        }
    }
}
