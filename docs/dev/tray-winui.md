# UltraCompress Desktop & Tray (`UltraCompress.Tray` — WinUI 3)

`UltraCompress.Tray` provides the **WinUI 3 Desktop Archive Manager** and persistent Windows system-tray companion connected to `UltraCompress.Engine`, backed by the cross-platform presentation layer in `UltraCompress.Desktop.Presentation`.

## Architecture

- **`src/UltraCompress.Desktop.Presentation`** (`net8.0`): Cross-platform ViewModels, workspace state, `.ucx`/`.zip` archive browser and selective extraction engine, multi-window content/entropy batch analyzer, SHA-256 duplicate detection, destination/free-space pre-flight manager, atomic crash-recovery journal, history KPI store, actionable error presenter, and four-mode consent gate validator.
- **`src/UltraCompress.Tray`** (`net8.0-windows10.0.19041.0`): WinUI 3 main desktop window + system tray companion (`H.NotifyIcon.WinUI` `2.3.2`, `Microsoft.WindowsAppSDK` `1.6.250108002`, `DISABLE_XAML_GENERATED_MAIN`).
- **`tests/UltraCompress.Desktop.Tests`** (`net8.0`): xUnit test suite validating batch staging, strategy routing, destination conflicts, selective extraction, pause/resume/crash-recovery workflows, error remediation cards, and mutating consent-gate safety invariants on both Linux and Windows.

## Core Capabilities

- **Single-Instance Activation:** Uses Windows App SDK `AppInstance.FindOrRegisterForKey("UltraCompress.Tray")` so secondary launches, Explorer context invocations, and `.ucx` file associations focus the active workspace cleanly.
- **System Tray Companion:** Uses [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) (`TaskbarIcon` + native `PopupMenu`) for background queue monitoring, quick pause/resume, and one-click workspace restore.
- **Engine IPC & Resilient Queue:** Connects over `\\.\pipe\UltraCompress.Engine` (`NamedPipeEngineClient`) and localhost gRPC (`17832`) to manage active, paused, completed, and crash-recovered jobs.
- **Workspace Views:**
  1. **Compress & Batch:** Drag-and-drop files/folders, Add Files / Add Folder pickers, select among the 5 lossless profiles (`Instant`, `Balanced`, `Archive`, `Tiny`, `Laboratory` with `100% Bit-Exact Lossless` SLA badges), inspect live Strategy Justification cards, view SHA-256 duplicate detection savings, visual Before / Estimated After size comparison bar, and configure destination free-space/conflict policies (`Rename`, `Overwrite`, `Skip`).
  2. **Archive Browser & Selective Extractor:** Open `.ucx` and `.zip` multi-file archives in O(1) manifest time, search/filter directory trees and per-entry codecs/SHA-256 status, verify archive integrity, and run selective file/folder extraction.
  3. **Queue & Crash Recovery Center:** Live throughput (`MB/s`), ETA, priority reorder, cooperative chunk-level pause/resume/cancel, automatic `.part` crash recovery (`Resume` or `Discard Temp`), and actionable error remediation cards.
  4. **Compression History View:** Searchable ledger of completed jobs, cumulative space saved KPI cards, before/after comparison, and **Open Folder** / **Extract / Browse** actions.
  5. **Optimization & Data Hygiene Studio:** Strictly separated UI for `LossyOptimization`, `FormatConversion`, and `DataRemoval` enforcing mandatory consent checkboxes, itemized impact/preview summaries, and a default-enabled **Preserve Original File** safety lock.

## Run Locally (Windows 10/11)

```powershell
# Terminal 1 — start Engine host (or run installed Windows Service)
dotnet run -c Release --project src/UltraCompress.Engine

# Terminal 2 — start WinUI 3 Desktop & Tray
dotnet run -c Release --project src/UltraCompress.Tray -p:Platform=x64

# Start minimized in system tray only:
dotnet run -c Release --project src/UltraCompress.Tray -p:Platform=x64 -- --tray
```

Engine rolling logs write to `%ProgramData%\UltraCompress\logs\engine-*.log`. Third-party codec attributions (`Zstandard`, `7-Zip`, `Brotli`, `libdeflate`) are surfaced in `THIRD_PARTY_NOTICES.txt`.
