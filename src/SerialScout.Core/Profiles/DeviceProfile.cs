namespace SerialScout.Core.Profiles;

/// <summary>
/// A locally stored mapping from stable USB identity to a human name and preferred line
/// settings. Everything lives in the local SQLite store (<c>SerialScout.Core.Storage</c>);
/// profiles never leave the machine.
/// </summary>
public sealed record DeviceProfile
{
    /// <summary>Stable local row id; <c>0</c> until the profile has been created in the store.</summary>
    public long Id { get; init; }

    /// <summary>Unique human-readable profile name.</summary>
    public required string Name { get; init; }

    /// <summary>Identity rule used for conservative matching.</summary>
    public required ProfileMatchRule Rule { get; init; }

    /// <summary>Preferred line settings applied when a session binds to this profile.</summary>
    public LineSettings LineSettings { get; init; } = LineSettings.Default;

    /// <summary>Free-form user notes.</summary>
    public string? Notes { get; init; }

    /// <summary>UTC creation time assigned by the store.</summary>
    public DateTimeOffset? CreatedUtc { get; init; }

    /// <summary>
    /// UTC time the matcher last positively identified this device, or <see langword="null"/>
    /// when it has never been seen. Drives staleness demotion in <see cref="ProfileMatcher"/>.
    /// </summary>
    public DateTimeOffset? LastSeenUtc { get; init; }
}
