using System.Text;
using SerialScout.Core.Profiles;
using SerialScout.Core.Sessions;
using SerialScout.Core.Storage;

namespace SerialScout.App;

/// <summary>
/// Non-GUI packaged-build check. It deliberately avoids Avalonia initialization and
/// serial discovery, and uses a unique temporary directory that is removed on exit.
/// </summary>
internal static class PackagedSmoke
{
    internal static int Run(TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var root = Path.Combine(Path.GetTempPath(), $"serial-scout-smoke-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "profiles.sqlite");

        var exitCode = 0;
        try
        {
            Directory.CreateDirectory(root);
            long profileId;
            long sessionId;

            using (var store = new ProfileStore(databasePath))
            {
                var profile = store.CreateProfile(new DeviceProfile
                {
                    Name = "Packaged smoke profile",
                    Rule = new ProfileMatchRule(0xFFFF, 0x0001, serialFingerprint: "SMOKE-LOCAL-ONLY"),
                    LineSettings = new LineSettings(115200),
                    Notes = "Temporary packaged-build persistence check",
                });
                var started = DateTimeOffset.UtcNow;
                var session = store.CreateSession("SMOKE-NO-HARDWARE", started, profile.Id);
                store.AppendSessionEvent(
                    session.Id,
                    new LogEvent(started, LogEventDirection.Received, Encoding.UTF8.GetBytes("sqlite-round-trip")));
                store.EndSession(session.Id, started.AddSeconds(1));
                profileId = profile.Id;
                sessionId = session.Id;
            }

            using (var reopened = new ProfileStore(databasePath))
            {
                var profile = reopened.GetProfile(profileId);
                var session = reopened.ListSessions().SingleOrDefault(item => item.Id == sessionId);
                var captured = reopened.ListSessionEvents(sessionId).SingleOrDefault();
                if (profile?.Name != "Packaged smoke profile"
                    || session?.PortPath != "SMOKE-NO-HARDWARE"
                    || captured is null
                    || Encoding.UTF8.GetString(captured.Payload) != "sqlite-round-trip")
                {
                    throw new InvalidOperationException("SQLite data did not survive close and reopen.");
                }
            }

            output.WriteLine("PASS packaged smoke: SQLite create/write/close/reopen/read succeeded; serial discovery was not initialized");
        }
        catch (Exception exception)
        {
            error.WriteLine($"FAIL packaged smoke: {exception.GetType().Name}: {exception.Message}");
            exitCode = 1;
        }

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            output.WriteLine("PASS packaged smoke cleanup: temporary storage removed");
        }
        catch (Exception cleanupException)
        {
            error.WriteLine($"FAIL packaged smoke cleanup: {cleanupException.GetType().Name}: {cleanupException.Message}");
            exitCode = 1;
        }

        return exitCode;
    }
}
