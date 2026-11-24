# UltraCompress.Tray (WinUI 3)

## Responsibilities

- **Single instance** via Windows App SDK `AppInstance.FindOrRegisterForKey("UltraCompress.Tray")`; secondary activations focus the queue window.
- **Notify icon** via [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) **2.3.2** (latest stable for **net8.0-windows**; 2.4+ requires .NET 10). Requires **Microsoft.WindowsAppSDK 1.6.250108002+** — `TaskbarIcon`, native `PopupMenu` context menu.
- **Engine IPC** through `UltraCompress.Core.Ipc.NamedPipeEngineClient` → `\\.\pipe\UltraCompress.Engine`.

## Run locally (Windows)

```powershell
# Terminal 1 — engine (console host; install as service later)
dotnet run --project src/UltraCompress.Engine

# Terminal 2 — tray
dotnet run --project src/UltraCompress.Tray -p:Platform=x64
```

Tray polls engine status every 15s and on activation; use **Reconnect to engine** after a service restart.

## Queue window

- **Enqueue** path + profile (Balanced / Small / Tiny / Laboratory)
- **List / cancel** jobs via pipe (`listJobs`, `enqueueJob`, `cancelJob`, `resumeAll`)
- Engine logs: `%ProgramData%\UltraCompress\logs\engine-*.log` (Serilog)

## Next steps (Phase 2+)

- Taskbar progress, multi-state tray icons
- Wire enqueue to real Worker compression (not in-memory stub)
- Sparse package identity for toasts (see project blueprint)