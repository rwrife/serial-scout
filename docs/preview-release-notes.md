This is an unsigned preview build intended for evaluation.

Download the ZIP for Windows x64, or the DMG matching an Intel (`osx-x64`) or Apple
silicon (`osx-arm64`) Mac. Verify the download with `SHA256SUMS.txt`; the adjacent
provenance file records its source commit, tag, SDK, runner, RID, and signature status.

Windows binaries are unsigned. macOS apps are ad-hoc signed but not notarized. Security
or reputation prompts are expected. macOS serial backend issue #12 remains unresolved,
and automated smoke checks cover packaged SQLite persistence without opening serial
hardware; they do not cover GUI interaction, drivers, physical devices, or RX/TX.

See the repository README for installation, permissions, and troubleshooting details.
