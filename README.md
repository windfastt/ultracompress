# UltraCompress

Windows-first file compressor that prioritizes **smallest defensible output** over speed. Phase 1 scaffold: Core contracts, worker CLI with **zstd**, engine service shell, and WinUI tray stub.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 for **UltraCompress.Tray** (WinUI 3, Phase 2)
- Linux/macOS can build **Core**, **Worker**, **Engine**, and tests

## Build

```bash
cd src
dotnet restore UltraCompress.sln
dotnet build UltraCompress.sln
```

On non-Windows hosts, build individual projects (tray targets `net8.0-windows`):

```bash
dotnet build src/UltraCompress.Core/UltraCompress.Core.csproj
dotnet build src/UltraCompress.Worker/UltraCompress.Worker.csproj
dotnet test tests/UltraCompress.Core.Tests/UltraCompress.Core.Tests.csproj
```

## CLI

```bash
cd src/UltraCompress.Worker
dotnet run -- compress --input /path/to/file --profile Small
```

Profiles: `Balanced`, `Small`, `Tiny`, `Laboratory`, `Experimental` — see [docs/USER.md](docs/USER.md).

## Projects

| Project | Role |
|---------|------|
| `UltraCompress.Core` | Recipes, fingerprints, backends, profiles |
| `UltraCompress.Worker` | zstd adapter, round-trip verify, CLI |
| `UltraCompress.Engine` | Windows service host, localhost job gRPC (`:17832`), optional LAN worker gRPC (`:17833`) |
| `UltraCompress.WorkerAgent` | gRPC client: heartbeat, lease steps, run compress on LAN machines |
| `UltraCompress.Tray` | WinUI 3 placeholder |
| `UltraCompress.Core.Tests` | Unit tests |

## Engine (gRPC)

Runs as a console app on Linux/macOS for development, or as **Windows Service** `UltraCompress.Engine` when installed.

```bash
cd src/UltraCompress.Engine
dotnet run
# gRPC: localhost:17832 (HTTP/2)
```

RPC surface (`engine_v1.proto`): `Enqueue`, `GetJob`, `ListJobs`, `CancelJob`, `SetQueuePaused`, `GetQueueState`, `WatchJobs`.

Distributed workers (`worker_v1.proto`, optional): `Heartbeat`, `LeaseStep`, `ReportStepResult` — see [docs/DISTRIBUTED_WORKERS.md](docs/DISTRIBUTED_WORKERS.md). Builds on PR #3 localhost `EngineService`; worker port uses a shared token file.

## CI

GitHub Actions: `dotnet build` + `dotnet test` on `windows-latest`.
