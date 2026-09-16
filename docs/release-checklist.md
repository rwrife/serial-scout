# Preview release checklist

This checklist is the release acceptance record. Preview tags have the form
`vMAJOR.MINOR.PATCH-preview.ID`; the workflow rejects other versions. Packaging is
local-first and self-contained. It does not add accounts, telemetry, network runtime
dependencies, privileged helpers, or installers.

## Before tagging

- Confirm the intended commit passed the pull-request `build-and-test` and all three
  native `package-gate` jobs.
- Review the uploaded logs and TRX: locked restore, format verification, Release build,
  tests, package, and packaged smoke must all be successful.
- Confirm dependency lockfiles are committed and unchanged by `dotnet restore
  src/SerialScout.App/SerialScout.App.csproj --locked-mode`. The app project declares
  `win-x64`, `osx-x64`, and `osx-arm64`, so its lock contains all publish graphs.
- Review user-facing changes and the known limitations below. Do not imply hardware or
  GUI coverage from the headless smoke result.
- Create an annotated preview tag on that exact commit. Tagging/pushing is a maintainer
  action; repository scripts and workflows never create tags.

## Automated tagged-build gates

The tag workflow performs the following before a release can be created:

1. Validate the tag and derive the package version.
2. Restore locked dependencies, verify formatting, build Release, and produce test logs
   plus TRX on Windows and Apple silicon macOS.
3. On native runners, invoke the same package scripts used by pull requests:
   `package-windows.ps1` for `win-x64`, and `package-macos.sh` for both macOS RIDs.
4. Extract the ZIP or mount the DMG and run `--packaged-smoke`. This bypasses Avalonia
   and serial discovery, writes a profile/session/event to a unique temporary SQLite
   database, closes and reopens it, verifies the data, and removes temporary storage.
5. Generate `SHA256SUMS.txt`, require the exact seven-file release set, re-check every
   hash, and validate provenance fields before upload.
6. Use the job-scoped `contents: write` permission only for the publisher. All build and
   package jobs retain `contents: read`. The publisher verifies that the pushed tag
   exists and creates a GitHub prerelease; it does not move or create tags.

Expected release assets for version `VERSION`:

```text
SHA256SUMS.txt
SerialScout-VERSION-win-x64.zip
SerialScout-VERSION-win-x64.provenance.txt
SerialScout-VERSION-osx-x64.dmg
SerialScout-VERSION-osx-x64.provenance.txt
SerialScout-VERSION-osx-arm64.dmg
SerialScout-VERSION-osx-arm64.provenance.txt
```

Each provenance record must identify the exact artifact, 40-character commit, tag, SDK
version, runner image, architecture matched to the RID, RID, self-contained status, and
signature status. macOS records also contain identical three-component numeric
`CFBundleShortVersionString` and `CFBundleVersion` values; the preview suffix remains
in the assembly informational version. Evidence logs and TRX remain workflow artifacts,
not release assets.

## Local packaging reproduction

Run each command on its named operating system. Cross-publishing is not equivalent to
the native package and smoke gate.

Windows x64, PowerShell 7:

```powershell
.\scripts\package-windows.ps1 -Version 0.1.0-preview.1
.\scripts\smoke-windows.ps1 -Package artifacts\release\SerialScout-0.1.0-preview.1-win-x64.zip
```

macOS, once on an Intel host and once on Apple silicon with the matching RID:

```bash
set -o pipefail
./scripts/package-macos.sh 0.1.0-preview.1 osx-arm64 2>&1 | tee package-osx-arm64.log
set -o pipefail
./scripts/smoke-macos.sh artifacts/release/SerialScout-0.1.0-preview.1-osx-arm64.dmg 2>&1 | tee smoke-osx-arm64.log
```

The scripts locked-restore the complete declared RID graph immediately before the
selected RID's `publish --no-restore`, so publish never silently changes dependency
resolution. Do not add `--runtime` to that restore: NuGet treats it as a narrowed graph
and correctly rejects the all-RID lockfile.

## Post-publish review and manual gaps

- Download all seven assets into an otherwise empty directory and run
  `./scripts/verify-release-assets.sh VERSION DIRECTORY EXPECTED_COMMIT_SHA` on a Unix
  host with GNU `sha256sum`. Confirm it reports the exact list, hashes, and provenance
  as valid.
- On clean Windows 10/11 x64, extract to a non-administrator directory, review the
  expected unsigned SmartScreen flow, open the GUI, save/reopen a profile, and test a
  representative physical serial adapter including RX/TX and busy-port recovery.
- On clean Intel and Apple silicon Macs, mount the DMG, drag the app to Applications,
  review the expected Gatekeeper flow, open the GUI, and record the result. With a
  representative physical adapter, test RX/TX, timeout, cancellation, unplug/replug,
  busy-port recovery, and automatic reconnect. For reset-sensitive boards, observe DTR/RTS
  during open, close, and reconnect: the app clears termios modem-flow and `HUPCL` flags but
  cannot prevent every driver or adapter from pulsing control lines.
- Check app naming/version display and confirm no network request, account prompt,
  telemetry, administrator prompt, or unexpected storage location appears.
- Record OS version, CPU, USB serial chipset/driver, GUI result, hardware result, and
  any workaround in release notes. Never mark an unperformed manual check as passed.

Known preview limitations:

- Windows is unsigned; macOS is ad-hoc signed and not notarized.
- Native macOS PTY tests do not verify physical USB-serial chipsets, vendor drivers,
  electrical behavior, DTR/RTS pulses, or board reset behavior.
- Automated checks do not cover GUI interaction, Gatekeeper/SmartScreen user flows,
  physical attach/detach, drivers, device contention, or real serial traffic.
- Only Windows x64 and macOS x64/arm64 are packaged. There is no installer, automatic
  update mechanism, or compatibility claim for Linux or Windows ARM64.
