namespace SerialScout.Core.Privacy;

/// <summary>Composable, local-only redaction categories offered before export.</summary>
[Flags]
public enum RedactionPreset
{
    /// <summary>Do not transform captured text.</summary>
    None = 0,

    /// <summary>Authentication headers, JWTs, API keys, and token-like assignments.</summary>
    Tokens = 1,

    /// <summary>IPv4, IPv6, and MAC addresses.</summary>
    NetworkIdentifiers = 2,

    /// <summary>Absolute Windows and Unix-style filesystem paths.</summary>
    FilePaths = 4,

    /// <summary>All built-in privacy transforms.</summary>
    ShareSafe = Tokens | NetworkIdentifiers | FilePaths,
}
