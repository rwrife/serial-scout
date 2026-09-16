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
implemented. Preview packages are produced by native Windows and macOS release gates;
the limitations below remain part of the preview compatibility contract.

## Preview downloads and installation

Preview builds are self-contained: installing the .NET SDK is not required. Download
the package for your CPU from the
[GitHub Releases page](https://github.com/rwrife/serial-scout/releases). Every preview
also includes `SHA256SUMS.txt` and a provenance record for each package. Verify the
package hash against the matching line in `SHA256SUMS.txt` before running it.

### Windows 10/11 x64 (portable ZIP)

1. Download `SerialScout-VERSION-win-x64.zip`, extract it to a user-writable folder,
   and run `SerialScout.App.exe`. Keep all extracted files together.
2. Windows SmartScreen may warn because preview binaries are unsigned. Choose **More
   info** and **Run anyway** only after verifying the checksum and that the download
   came from this repository. No administrator access is required.
3. Windows may install a vendor USB-serial driver when a board is first attached. If a
   port is missing, check Device Manager; if it is busy, close other terminal/IDE tools.

PowerShell checksum example:

```powershell
Get-FileHash .\SerialScout-VERSION-win-x64.zip -Algorithm SHA256
Select-String 'SerialScout-VERSION-win-x64.zip' .\SHA256SUMS.txt
```

### macOS 12+ (DMG)

Choose `osx-arm64` for Apple silicon or `osx-x64` for an Intel Mac. Open the DMG and
drag **Serial Scout.app** to Applications. Preview apps are only ad-hoc signed, not
Developer ID signed or notarized, so Gatekeeper may block the first launch. After
verifying the checksum and source, Control-click the app, choose **Open**, then confirm.
If macOS still retains the quarantine prompt, advanced users can remove it explicitly:

```bash
xattr -dr com.apple.quarantine '/Applications/Serial Scout.app'
```

That command weakens a macOS safety check for this app; do not use it on an unverified
download. Serial access uses `/dev/cu.*` devices and should not require administrator
access. Close other programs holding the device and install only the USB-serial driver
provided by the board/chip vendor when macOS does not create a port.

macOS sessions use a native Darwin `termios`/`poll` backend, not `System.IO.Ports`.
The serial descriptor is opened with exclusive tty ownership and is nonblocking. Blocked
reads/writes poll in slices of at most 25 ms, so cancellation and close are observed within
one OS poll slice plus thread scheduling delay. The tradeoff is that each blocked operation
occupies one worker and wakes to check its flags every 25 ms. The macOS UI offers standard
baud rates through 230400; a saved profile containing an incompatible higher rate fails
through the normal structured open-failure path. The backend requests raw local mode,
enables the receiver, and enables input parity checking for even/odd parity (while
disabling it for no parity). It clears all hardware-flow-control flags and `HUPCL`; Serial
Scout does not explicitly assert DTR or RTS.

With parity checking enabled, Darwin represents parity or framing error bytes as NUL in
this raw configuration; Serial Scout stores those received bytes unchanged. These settings
reduce avoidable control-line transitions, but opening or closing a serial device can still
make a driver or USB-serial adapter pulse DTR/RTS. Some Arduino-class boards interpret
that pulse as reset. Software cannot promise pulse-free open across all drivers and
adapters, and automatic reconnect necessarily opens the device again. Test reset-sensitive
hardware deliberately and disable auto-reconnect when an unexpected reset would be unsafe.

Checksum example:

```bash
shasum -a 256 SerialScout-VERSION-osx-arm64.dmg
grep 'SerialScout-VERSION-osx-arm64.dmg' SHA256SUMS.txt
```

### Preview limitations and troubleshooting

- Windows packages are unsigned. macOS apps are ad-hoc signed but not notarized; both
  platforms can show reputation or security prompts.
- Native CI exercises the macOS backend with a pseudo-terminal, including line settings,
  RX/TX, timeout, cancellation, and hangup. This is not physical-device verification and
  does not establish compatibility with any USB-serial chipset, vendor driver, or board.
- CI verifies launch-free SQLite persistence from the packaged executable. It cannot
  automate GUI interaction, device drivers, unplug/replug behavior, busy-port recovery,
  or real RX/TX with the wide range of USB serial chipsets. Those are manual checks.
- The application is local-only: no account, network service, cloud storage, telemetry,
  or elevated privileges are needed. Profile/session data locations are documented
  above. Delete that directory to reset local state after first closing the app.

For release evidence and the complete operator checklist, see
[docs/release-checklist.md](docs/release-checklist.md).

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

CI runs formatting, build, and test checks on Windows and Apple silicon macOS. Pull
requests additionally create and smoke the Windows x64, macOS Intel, and macOS Apple
silicon packages on matching native runners. Maintainers can reproduce those packages
with `scripts/package-windows.ps1` or `scripts/package-macos.sh`; see the release
checklist for exact commands and evidence expectations.
