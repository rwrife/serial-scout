namespace SerialScout.Core.Profiles;

/// <summary>
/// One local session-history row: when a port was used, optionally bound to the profile
/// that matched it. Rows are kept even when their profile is later deleted (the binding
/// becomes <see langword="null"/>) so usage history survives profile cleanup.
/// </summary>
public sealed record SessionMetadata
{
    /// <summary>Stable local row id; <c>0</c> until the row has been created in the store.</summary>
    public long Id { get; init; }

    /// <summary>Profile that matched at session time, or <see langword="null"/> when unbound.</summary>
    public long? ProfileId { get; init; }

    /// <summary>OS port path used for the session.</summary>
    public required string PortPath { get; init; }

    /// <summary>UTC time the session started.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>UTC time the session ended, or <see langword="null"/> while still open.</summary>
    public DateTimeOffset? EndedUtc { get; init; }

    /// <summary>Free-form user notes about the session.</summary>
    public string? Notes { get; init; }
}
