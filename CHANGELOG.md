# Changelog

All notable changes to this project are documented in this file.

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased] — v0.4.0

### Planned

- Merge open feature PRs (backends, probe, advisor, tournament, WiX installer)
- MSI bundle: Engine + Tray + VC++ redistributable
- Golden CI on Windows for zstd and 7z corpora
- Product site install quickstart and release artifacts

## [0.3.1] - 2026-11-24

### Added

- WinUI 3 system tray with job queue UI and profile picker
- `UltraCompress.Engine` Windows Service with named-pipe IPC and Serilog file logging
- `UltraCompress.Worker` CLI with zstd round-trip verification
- Core recipe schemas, profile defaults, and unit tests
- Benchmark smoke harness and Windows CI workflow
- GitHub Pages product site and release workflow (draft releases on tag)
- MIT license and contributor documentation

### Changed

- Public docs and README oriented toward the integrated desktop stack

## [0.2.0] - 2026-11-24

### Added

- Initial solution scaffold, core types, and worker stubs