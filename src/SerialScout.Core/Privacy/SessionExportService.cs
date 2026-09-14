using System.Text.Json;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;

namespace SerialScout.Core.Privacy;

/// <summary>Supported selected-session export representations.</summary>
public enum SessionExportFormat
{
    /// <summary>Human-readable timestamped RX/TX lines.</summary>
    PlainText,

    /// <summary>Versioned deterministic structured JSON.</summary>
    Json,
}

/// <summary>An immutable export preview; writing is disabled until <see cref="Confirm"/> is called.</summary>
/// <param name="Content">Exact content shown to the user and subsequently written.</param>
/// <param name="Format">Chosen representation.</param>
/// <param name="Preset">Applied privacy transforms.</param>
/// <param name="IsConfirmed">Whether the user explicitly accepted this exact preview.</param>
public sealed record SessionExportPreview(
    string Content,
    SessionExportFormat Format,
    RedactionPreset Preset,
    bool IsConfirmed = false)
{
    /// <summary>Returns a copy authorized for local file output.</summary>
    public SessionExportPreview Confirm() => this with { IsConfirmed = true };
}

/// <summary>Builds selected-session previews and writes only explicitly confirmed content.</summary>
public static class SessionExportService
{
    /// <summary>Current JSON schema version.</summary>
    public const int JsonFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    /// <summary>Creates the exact local preview for a session and an event snapshot.</summary>
    public static SessionExportPreview CreatePreview(
        SessionMetadata session,
        IReadOnlyList<LogEvent> events,
        RedactionPreset preset,
        SessionExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(events);

        var transformed = LogRedactor.Apply(events, preset);
        var content = format switch
        {
            SessionExportFormat.PlainText => SessionLogRenderer.Render(transformed),
            SessionExportFormat.Json => RenderJson(session, transformed, preset),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return new SessionExportPreview(content, format, preset);
    }

    /// <summary>Writes a previously previewed and explicitly confirmed export.</summary>
    public static Task WriteAsync(SessionExportPreview preview, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!preview.IsConfirmed)
        {
            throw new InvalidOperationException("Review and confirm the export preview before writing it.");
        }

        return AtomicFileWriter.WriteNewTextAsync(path, preview.Content, cancellationToken);
    }

    private static string RenderJson(SessionMetadata session, IReadOnlyList<LogEvent> events, RedactionPreset preset)
    {
        var document = new
        {
            formatVersion = JsonFormatVersion,
            session = new
            {
                id = session.Id,
                profileId = session.ProfileId,
                portPath = LogRedactor.RedactText(session.PortPath, preset),
                startedUtc = session.StartedUtc.ToUniversalTime(),
                endedUtc = session.EndedUtc?.ToUniversalTime(),
                notes = session.Notes is null ? null : LogRedactor.RedactText(session.Notes, preset),
            },
            events = events.Select(logEvent => new
            {
                utc = logEvent.Utc.ToUniversalTime(),
                direction = logEvent.Direction == LogEventDirection.Received ? "received" : "sent",
                text = SessionLogRenderer.Decode(logEvent.Payload),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }
}
