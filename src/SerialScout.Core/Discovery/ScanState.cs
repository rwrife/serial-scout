namespace SerialScout.Core.Discovery;

/// <summary>
/// Explicit per-port health state reported by a discovery scan.
/// </summary>
public enum ScanState
{
    /// <summary>The port looks usable and can likely be opened.</summary>
    Ready,

    /// <summary>The port exists but is held by another process or session.</summary>
    Busy,

    /// <summary>The OS refused access for the current user/context.</summary>
    PermissionDenied,

    /// <summary>Health or identity could not be determined from platform metadata.</summary>
    Unknown,
}
