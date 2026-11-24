# Implementation checklist

Track delivery against `main`. Update checkboxes when work lands in merged PRs.

## Phase 0 — Planning

- [x] North star and profiles defined
- [x] Architecture diagram and process split
- [x] Pipeline DSL sketched
- [x] Compression backend matrix validated
- [x] Windows service + tray patterns validated
- [ ] Open decisions in blueprint §11 finalized in public docs
- [x] GitHub repository published (`windfastt/ultracompress`)

## Phase 1 — Core engine

- [x] `UltraCompress.Core` — `FileFingerprint`, `PipelineRecipe`, validation
- [x] Recipe JSON schema + unit tests
- [x] Worker host: child process, timeout, job runner
- [x] Adapters: zstd compress/decompress roundtrip
- [ ] Adapters: 7z single-file archive output (feat branch)
- [x] CLI: worker `compress` with profiles
- [ ] Golden-file tests (3 corpora: text, binary, mixed) on CI

## Phase 2 — Windows service + tray

- [x] `UltraCompress.Engine` Windows Service
- [x] Named pipe localhost API
- [x] `UltraCompress.Tray` single-instance + notify icon
- [x] Queue UI: add, pause, cancel, profile picker
- [ ] Taskbar progress integration
- [x] Serilog rolling logs under ProgramData

## Phase 3 — Analysis + search

- [ ] Probe: size, hashes, entropy, extension
- [ ] Magic / content type detection
- [ ] Advisor ruleset v1 (extension + entropy gates)
- [ ] SQLite recipe store + migrations
- [ ] Tournament mode (N candidates, time budget)
- [ ] Bandit search (Laboratory flag)
- [ ] “Pin recipe” from tray UI

## Phase 4 — Integrations

- [ ] Explorer shell context menu
- [ ] gRPC worker registration (LAN token file)
- [ ] Remote worker sample client
- [ ] Block-split job for huge files (optional)

## Phase 5 — Installer + experimental

- [ ] WiX: service, tray autostart, firewall rules (if distributed)
- [ ] VC++ redist bootstrapper
- [ ] Plugin SDK sample plugin
- [ ] Experimental profile gate + crash telemetry opt-in
- [ ] PAQ / external codec subprocess bridge (optional download)

## CI / quality

- [x] GitHub Actions: build, test on `windows-latest`
- [x] Benchmark smoke project (`tests/UltraCompress.Benchmarks`)
- [ ] GitHub Actions: artifact MSI
- [ ] Code signing workflow (manual secret)
- [ ] Dependabot for .NET

## Documentation

- [x] User: profiles and risk tiers (`docs/USER.md`)
- [x] Product marketing site (`docs/site/` → GitHub Pages)
- [ ] Dev: writing preprocessors and plugins
- [ ] Operator: distributed workers