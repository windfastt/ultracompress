# UltraCompress Desktop & Tray (`UltraCompress.Tray` — WinUI 3)

`UltraCompress.Tray` provides the **WinUI 3 Desktop Archive Manager** and persistent Windows system-tray companion connected to `UltraCompress.Engine`.

## Core Capabilities

- **Single-Instance Activation:** Uses Windows App SDK `AppInstance.FindOrRegisterForKey("UltraCompress.Tray")` so secondary launches, Explorer context invocations, and `.ucx` file associations focus the active workspace cleanly.
- **System Tray Companion:** Uses [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) (`TaskbarIcon` + native `PopupMenu`) for background queue monitoring, quick pause/resume, and one-click workspace restore.
- **Engine IPC & Resilient Queue:** Connects over `\\.\pipe\UltraCompress.Engine` (`NamedPipeEngineClient`) and localhost gRPC (`17832`) to manage active, paused, completed, and crash-recovered jobs backed by `%ProgramData%\UltraCompress\jobs.db`.
- **Workspace Views:**
  1. **Compress & Pack:** Drag-and-drop files/folders, select among the 5 lossless profiles (`Instant`, `Balanced`, `Archive`, `Tiny`, `Laboratory`), inspect live `StrategyDecisionReport` rationale cards, view duplicate detection savings, and configure destination free-space/conflict policies.
  2. **Archive Browser:** Open `.ucx` multi-file archives in O(1) manifest time, inspect directory trees and per-entry codecs/checksums, verify archive integrity, and run selective file/folder extraction.
  3. **Queue & Recovery:** Live throughput (`MB/s`), ETA, cooperative chunk-level pause/resume/cancel, automatic `.part` crash recovery, and actionable error remediation cards.
  4. **History & Savings:** Searchable ledger of completed jobs, cumulative GiB saved, and cached SQLite recipes (`recipes.db`).
  5. **Optimization & Data Hygiene Studio:** Strictly separated UI for `LossyOptimization`, `FormatConversion`, and `DataRemoval` enforcing mandatory consent checkboxes, visual/manifest previews, and locked-by-default original file preservation.

## Run Locally (Windows 10/11)

```powershell
# Terminal 1 — start Engine host (or run installed Windows Service)
dotnet run -c Release --project src/UltraCompress.Engine

# Terminal 2 — start WinUI 3 Desktop & Tray
dotnet run -c Release --project src/UltraCompress.Tray -p:Platform=x64
```

Engine rolling logs write to `%ProgramData%\UltraCompress\logs\engine-*.log`. Third-party codec attributions (`Zstandard`, `7-Zip`, `Brotli`, `libdeflate`) are surfaced in **Settings → Legal** and `THIRD_PARTY_NOTICES.txt`.
