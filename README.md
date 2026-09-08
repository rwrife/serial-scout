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

## Current status

Scaffold and backlog only. No production app, binary releases, or compatibility claims yet.

## Milestones

- M1: Core serial discovery + profile matching library
- M2: Session terminal + log capture
- M3: Desktop UI polish + accessibility baseline
- M4: Export/backup + packaging for Windows/macOS

## Development quickstart (planned)

```bash
# prerequisites: .NET 8 SDK
# clone
 git clone https://github.com/rwrife/serial-scout.git
 cd serial-scout

# planned project bootstrap (to be created in issue #1)
 dotnet --info
```

Until issue backlog execution starts, this repository is documentation-only.
