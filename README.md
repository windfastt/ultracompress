# UltraCompress

[![CI](https://github.com/windfastt/ultracompress/actions/workflows/ci.yml/badge.svg)](https://github.com/windfastt/ultracompress/actions/workflows/ci.yml)
[![MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![v0.3.1](https://img.shields.io/badge/version-0.3.1-2dd4bf)

Windows compression **platform** — Engine service, WinUI tray, recipes, benchmarks, and GitHub Pages site.

**Docs:** [USER](docs/USER.md) · [Tray](docs/dev/tray-winui.md) · [Testing](docs/testing.md) · [Performance](docs/performance.md)

**Site:** deploy via Actions ([pages.yml](.github/workflows/pages.yml)) → `https://windfastt.github.io/ultracompress/`

## Build
```powershell
dotnet build src/UltraCompress.sln -c Release
dotnet test src/UltraCompress.sln -c Release
```

## Release
Tag `v0.3.1` triggers draft [release.yml](.github/workflows/release.yml).

MIT — see [CONTRIBUTING.md](CONTRIBUTING.md), [CHANGELOG.md](CHANGELOG.md).
