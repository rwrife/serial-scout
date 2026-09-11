namespace SerialScout.Core.Sessions;

/// <summary>
/// Structured session lifecycle transitions a UI (issue #5) subscribes to. Every
/// reason string constant below is a stable machine tag; human text stays at the UI
/// boundary.
/// </summary>
public enum SessionEventType
{
    /// <summary>The link opened successfully and monitoring started.</summary>
    Connected,

    /// <summary>The link closed cleanly (user stop or engine stop).</summary>
    Disconnected,

    /// <summary>
    /// Unexpected drop detected; a reconnect attempt is scheduled. A
    /// <see cref="SessionEventType.Failed"/> event with reason
    /// <see cref="SessionEventReasons.ReconnectAbandoned"/> ends the chain when the
    /// retry limit is reached.
    /// </summary>
    ReconnectAttempt,

    /// <summary>A transition failed permanently or retries were abandoned.</summary>
    Failed,
}

/// <summary>Stable machine-readable reason tags carried on lifecycle events.</summary>
public static class SessionEventReasons
{
    /// <summary>Open succeeded (attached to <see cref="SessionEventType.Connected"/>).</summary>
    public const string OpenSucceeded = "open-succeeded";

    /// <summary>The user closed the session deliberately.</summary>
    public const string UserRequested = "user-requested";

    /// <summary>The OS reported the device vanished while open.</summary>
    public const string DeviceGone = "device-gone";

    /// <summary>Opening the port failed with an OS error.</summary>
    public const string OpenFailed = "open-failed";

    /// <summary>An in-flight transfer hit an OS error.</summary>
    public const string IoError = "io-error";

    /// <summary>Automatic reconnect was disabled for this session.</summary>
    public const string ReconnectDisabled = "reconnect-disabled";

    /// <summary>The reconnect retry limit was reached.</summary>
    public const string ReconnectAbandoned = "reconnect-abandoned";

    /// <summary>Automatic reconnect succeeded on attempt n.</summary>
    public const string ReconnectSucceeded = "reconnect-succeeded";
}

/// <summary>
/// One structured lifecycle event. <see cref="Utc"/> comes from an injectable clock so
/// tests assert exact ordering.
/// </summary>
/// <param name="Utc">UTC instant the transition was observed.</param>
/// <param name="Type">Lifecycle transition.</param>
/// <param name="PortPath">OS port path the transition applies to.</param>
/// <param name="Reason">Stable machine tag from <see cref="SessionEventReasons"/>.</param>
/// <param name="Detail">OS exception message or supplemental text when available.</param>
/// <param name="Attempt">1-based reconnect attempt number; <c>0</c> for non-reconnect events.</param>
public sealed record SessionEvent(
    DateTimeOffset Utc,
    SessionEventType Type,
    string PortPath,
    string Reason,
    string? Detail = null,
    int Attempt = 0);
