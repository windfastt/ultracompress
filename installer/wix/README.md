# WiX v4 installer

Produces:

| Output | Project | Description |
|--------|---------|-------------|
| `UltraCompress.msi` | `UltraCompress.Package` | Per-machine install under `Program Files\UltraCompress\` |
| `UltraCompress-Setup.exe` | `UltraCompress.Bundle` | Bootstrapper that chains the MSI |

## Layout

- `Engine\UltraCompress.Engine.exe` — Windows service `UltraCompressEngine` (auto-start)
- `Tray\UltraCompress.Tray.exe` — Start Menu + desktop shortcuts (Phase 2 stub)
- `Worker\UltraCompress.Worker.exe` — CLI worker
- `THIRD_PARTY_NOTICES.txt` — LGPL / third-party attribution (includes Zstandard)

Self-contained `win-x64` single-file publishes are embedded at build time.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WiX Toolset 4](https://wixtoolset.org/) via NuGet (`WixToolset.Sdk` — restored automatically)
- **Visual C++ 2015–2022 Redistributable (x64)** on the target machine (checked at MSI install)

## Build

From the repo root on Windows:

```powershell
dotnet build installer/wix/UltraCompress.Bundle/UltraCompress.Bundle.wixproj -c Release
```

Artifacts:

- `installer/wix/UltraCompress.Package/bin/Release/UltraCompress.msi`
- `installer/wix/UltraCompress.Bundle/bin/Release/UltraCompress-Setup.exe`

## CI

GitHub Actions runs the bundle build on `windows-latest` after `dotnet test`.