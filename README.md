# UltraCompress

[![CI](https://github.com/windfastt/ultracompress/actions/workflows/ci.yml/badge.svg)](https://github.com/windfastt/ultracompress/actions/workflows/ci.yml)
[![MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![v0.3.1](https://img.shields.io/badge/version-0.3.1-2dd4bf)

Windows compression **platform** — Engine service, WinUI tray, worker CLI (**zstd** + **7z `7za` subprocess**), recipes, benchmarks, and GitHub Pages site.

**Docs:** [USER](docs/USER.md) · [Tray](docs/dev/tray-winui.md) · [Testing](docs/testing.md) · [Performance](docs/performance.md)

**Site:** [pages.yml](.github/workflows/pages.yml) publishes to branch `gh-pages` → `https://windfastt.github.io/ultracompress/`

## Build
```powershell
dotnet build src/UltraCompress.sln -c Release
dotnet test src/UltraCompress.sln -c Release
```

## CLI

```bash
cd src/UltraCompress.Worker
dotnet run -- compress --input /path/to/file --profile Small
```

Profiles: `Balanced`, `Small`, `Tiny`, `Laboratory`, `Experimental` — see [docs/USER.md](docs/USER.md). **Laboratory** defaults to LZMA2 **7z** via bundled `tools/7z/` (`7za`); see [tools/7z/README.md](tools/7z/README.md) and [docs/legal/THIRD_PARTY_NOTICES.md](docs/legal/THIRD_PARTY_NOTICES.md).

## Engine

`UltraCompress.Engine` — Windows service with **gRPC** on `localhost:17832` (HTTP/2) and **named-pipe** IPC for the tray. See `src/UltraCompress.Engine/Protos/engine_v1.proto`.

`UltraCompress.Shell` — Explorer context-menu enqueue stub (sparse MSIX manifest); see [docs/SHELL_EXTENSION.md](docs/SHELL_EXTENSION.md).

## Release
Tag `v0.3.1` triggers draft [release.yml](.github/workflows/release.yml).

MIT — see [CONTRIBUTING.md](CONTRIBUTING.md), [CHANGELOG.md](CHANGELOG.md).
