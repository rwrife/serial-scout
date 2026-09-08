# Serial Scout — Implementation Plan

## 1) Scope and architecture

Serial Scout is a local-first desktop utility that maps unstable OS serial-port names to stable user profiles and launches repeatable terminal sessions with structured local logging.

### Planned architecture

- **SerialScout.Core** (UI-free domain library)
  - Device discovery adapters
  - Profile matching engine
  - Session orchestration and log pipeline
  - Export/backup services
- **SerialScout.App** (Avalonia desktop UI)
  - Device list, profile editor, terminal view, log browser
  - Accessibility-first command surfaces and keyboard navigation
- **SerialScout.Cli** (optional companion CLI)
  - Non-interactive profile/session/log export commands

## 2) Technology choices (with rationale)

- **.NET 8**: mature cross-platform runtime and tooling.
- **Avalonia UI**: one codebase for Windows + macOS desktop UI.
- **System.IO.Ports + adapter abstraction**: portable serial support with platform-specific fallback if needed.
- **SQLite**: local durable storage for profiles/session metadata.
- **Plaintext/JSON export**: auditable logs and automation-friendly interchange.

## 3) Milestones and dependency order

1. **Project skeleton + CI baseline**
   - Solution structure, lint/test workflow, release artifacts scaffold.
2. **Core discovery + profile model**
   - Enumerate ports, normalize metadata, persist profiles.
3. **Deterministic profile matcher**
   - Matching/ranking rules with explicit confidence and unknown states.
4. **Session terminal + logging**
   - Connect/disconnect/reconnect, send/receive paths, local rolling logs.
5. **Desktop UX + accessibility pass**
   - Keyboard-only flow, screen-reader labels, clear busy/error states.
6. **Export/backup + privacy controls**
   - Redaction presets, session export, profile import/export.
7. **Packaging/distribution**
   - Windows portable + installer, macOS app bundle/notarization-ready outputs.

## 4) Testing strategy

- Unit tests for matcher, parsing, and log-redaction primitives.
- Integration tests using virtual loopback/fake serial adapters.
- Fixture-driven tests for ambiguous identity cases.
- Smoke tests for export/import compatibility.
- Manual validation matrix on Windows 10/11 and current macOS.

## 5) Packaging and distribution plan

- GitHub Actions build matrix for Windows and macOS.
- Produce unsigned preview artifacts first; add signing/notarization later.
- Keep reproducible version metadata in release artifacts.

## 6) Risks

- OS-specific serial metadata differences can reduce match confidence.
- macOS permission prompts and device-lock contention need careful UX.
- High-volume log capture may impact UI responsiveness without buffering.

## 7) Explicit non-goals

- Cloud account infrastructure and remote log collection.
- Device firmware updates/flashing workflows.
- Automated driver repair or elevated system tuning.
- Any claim of electrical hardware validation.
