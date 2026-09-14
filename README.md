# Serial Scout

Local-first desktop utility for makers to save USB serial device profiles, auto-detect reconnecting boards, and reopen the right terminal settings with clean local logs and export.

## Motivation

Developers and hardware hobbyists often switch between ESP32/RP2040/Arduino devices that re-enumerate under different COM/tty names. Repeating baud/line-ending settings and hunting for the correct port slows debugging and introduces mistakes.

## Target users

- Embedded developers working across multiple USB serial boards
- Makers doing firmware bring-up and field diagnostics
- Support engineers who need reproducible local serial capture logs

## Concrete use cases

1. Save a profile for each board (VID/PID + optional serial fingerprint + baud/format).
2. Reconnect a board and let Serial Scout identify the best-matching profile.
3. Launch a terminal session with known-good settings in one click.
4. Capture timestamped logs for a session and export sanitized text/JSON bundles.
5. Compare current vs previous sessions to spot regressions in boot and runtime output.

## Intended end-to-end workflow

1. **Discover devices**: scan available serial ports and collect metadata.
2. **Create profile**: choose match rules, default port settings, and log preferences.
3. **Connect session**: open terminal with profile defaults (baud, parity, data bits, stop bits, flow control, line endings).
4. **Capture evidence**: save rolling local logs and mark important events.
5. **Export/share**: export selected session logs with optional redaction presets.

## MVP feature list

- Cross-platform desktop app (Windows 10/11, macOS)
- Serial device discovery with capability/health states (`ready`, `busy`, `permission-denied`, `unknown`)
- Profile manager with conservative identity matching
- Embedded terminal view with send presets and reconnect controls
- Session log capture with local search and export
- Offline-first operation with user-owned local storage

## Non-goals (MVP)

- Cloud sync, user accounts, or telemetry upload
- Firmware flashing and bootloader tooling replacement
- Driver installation/repair automation
- Privileged kernel/device modifications
- Electrical validation/certification of cables or hardware

## Privacy, permissions, and data storage

- **Local-first**: data stays on device by default.
- **Permissions**: serial-port access only; no microphone/camera/location/network permission required for core workflow.
- **Sensitive data**: raw serial logs can include tokens or identifiers. Exports default to redaction helpers and explicit user review.
- **Data ownership**: profile DB and logs are user-accessible and exportable.

### Storage and retention details

Serial Scout has no cloud account, sync, analytics, or network export path. Its only
durable application data is `profiles.sqlite`, including profiles, session metadata,
and raw RX/TX event bytes:

- Windows: `%LOCALAPPDATA%\SerialScout\profiles.sqlite`
- macOS and other Unix hosts: `~/.local/share/serial-scout/profiles.sqlite`

The live terminal also keeps a bounded in-memory rolling view (1 MiB of payload by
default); that view disappears when the process exits. Durable sessions are retained
until the user opens **Privacy & Export**, chooses a newest-session limit, acknowledges
that older raw traffic will be permanently deleted, and applies retention. Deleting a
profile does not delete its sessions; those sessions become unbound.

Selected-session export is always local and requires an explicit preview and
confirmation. Plaintext uses stable UTC timestamp/RX/TX lines. JSON uses the documented
`formatVersion: 1` envelope with `session` metadata and ordered `events` (`utc`,
`direction`, `text`). Token, IP/MAC, path, and share-safe presets transform export
copies only; stored raw bytes are never edited. Redacted exports retain every original
event timestamp and direction. A marker is placed in the first event containing the
redacted value, and later events fully consumed by that match remain present with empty
text.

Profile/session backups use versioned JSON and include raw event payloads. They should
be treated as sensitive: the UI shows the exact document and requires acknowledgement
before save or restore. Restore treats files as untrusted, rejects unknown fields,
unsupported versions, malformed values, and oversized collections, assigns new local
IDs, and skips/report profile-name conflicts rather than overwriting existing data.
Export and backup saves never overwrite an existing destination; they stage a complete
file in the destination directory before an atomic no-clobber rename. The active
`profiles.sqlite` database and its `-wal`, `-shm`, and `-journal` sidecars are blocked
as destinations. Retention also protects the active terminal session even when it is
older than the selected newest-session count.

## Current status

The .NET 8 solution, Avalonia desktop shell, core library, test project, and
cross-platform CI baseline are in place. Cross-platform serial discovery is implemented
in `SerialScout.Core.Discovery` (Windows PnP + macOS ioreg adapters with a normalized
port model and explicit scan states); see
[docs/discovery-metadata.md](docs/discovery-metadata.md) for per-OS metadata limits.
Profile matching, session capture, local privacy/export, and backup workflows are
implemented; there are no binary releases or compatibility claims yet.

## Milestones

- M1: Core serial discovery + profile matching library
- M2: Session terminal + log capture
- M3: Desktop UI polish + accessibility baseline
- M4: Export/backup + packaging for Windows/macOS

## Development quickstart

```bash
# Prerequisite: .NET 8 SDK
git clone https://github.com/rwrife/serial-scout.git
cd serial-scout

# Restore exactly the dependency versions recorded in packages.lock.json.
dotnet restore SerialScout.sln --locked-mode

# Run the same quality gates used by CI.
dotnet format SerialScout.sln --verify-no-changes --no-restore
dotnet build SerialScout.sln --configuration Release --no-restore
dotnet test SerialScout.sln --configuration Release --no-build

# Launch the desktop shell.
dotnet run --project src/SerialScout.App/SerialScout.App.csproj
```

CI runs formatting, build, and test checks on both Windows and macOS.
