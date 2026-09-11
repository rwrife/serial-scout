using SerialScout.Core.Profiles;

namespace SerialScout.Core.Sessions;

/// <summary>
/// Fully-resolved parameters for one engine session. The service builds these from
/// profile defaults (baud/parity/stop from <c>DeviceProfile.LineSettings</c>) plus
/// explicit overrides; validation happens here so a session never starts on guesses.
/// </summary>
public sealed record SessionOptions
{
    /// <summary>OS port path to open, e.g. <c>COM7</c> or <c>/dev/cu.usbserial-110</c>.</summary>
    public required string PortPath { get; init; }

    /// <summary>Line settings applied on every (re)open.</summary>
    public LineSettings LineSettings { get; init; } = LineSettings.Default;

    /// <summary>Line ending appended to every text frame sent.</summary>
    public LineEnding LineEnding { get; init; } = LineEnding.LF;

    /// <summary>Profile whose defaults produced these options, when a profile matched.</summary>
    public long? ProfileId { get; init; }

    /// <summary>Rolling log budget in payload bytes.</summary>
    public int LogMaxBytes { get; init; } = DefaultLogMaxBytes;

    /// <summary>Send-history entries retained per port.</summary>
    public int HistoryCapacity { get; init; } = DefaultHistoryCapacity;

    /// <summary>Automatically reconnect after an unexpected drop.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>Maximum reconnect attempts before the session fails permanently.</summary>
    public int MaxReconnectAttempts { get; init; } = 5;

    /// <summary>Delay applied before reconnect attempt n (index 0 is the first retry).</summary>
    public Func<int, TimeSpan> ReconnectBackoff { get; init; } = DefaultBackoff;

    /// <summary>Idle timeout for one read step; the loop checks cancellation each tick.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Shared default budget: 1 MiB of retained traffic.</summary>
    public const int DefaultLogMaxBytes = 1 << 20;

    /// <summary>Shared default history depth.</summary>
    public const int DefaultHistoryCapacity = 50;

    /// <summary>Constant 500 ms backoff used when the caller does not supply one.</summary>
    public static TimeSpan DefaultBackoff(int attempt) => TimeSpan.FromMilliseconds(500);

    /// <summary>Validates the options, throwing for unusable combinations.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PortPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LogMaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HistoryCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReconnectAttempts);
        ArgumentNullException.ThrowIfNull(ReconnectBackoff);
        if (ReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout), "Read timeout must be positive.");
        }
    }
}
