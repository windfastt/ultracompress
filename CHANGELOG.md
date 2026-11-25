# Changelog

All notable changes to UltraCompress are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased] — v1.0.0 Commercial-Grade Readiness

### Added

- **Measurable Lossless `ProfileContract` SLAs:** Defined concrete throughput floors, peak RAM caps, high-entropy bypass thresholds, and candidate search budgets across all five **100% bit-exact lossless** profiles (`Instant`, `Balanced`, `Archive`, `Tiny`, `Laboratory`), verified by per-block `XXH64` and end-to-end `SHA-256` checksums.
- **Strict Four-Mode Data Safety Boundary (`OperationKind`):** Isolated `LosslessCompression` (default) from `LossyOptimization`, `FormatConversion`, and `DataRemoval`, requiring explicit `ConsentConfirmation`, fidelity/impact preview metadata, itemized removal manifests, and `PreserveOriginal = true` by default ([docs/profiles-and-safety.md](docs/profiles-and-safety.md)).
- **Multi-Window O(1) `ContentInspector` & `StrategyDecisionReport`:** Constant-memory 5-window stream inspection (`head`, `25%`, `50%`, `75%`, `tail`) detecting 30+ magic/container signatures, Shannon entropy, bigram predictability, and 4/8-byte stride correlation, paired with human-readable and JSON decision reports (`analyze --explain`).
- **Reversible Lossless Preprocessors:** `FastStreamBypass` (`Store` for high-entropy media), `ByteShuffleTransform` (`stride = 2/4/8`), `DeltaTransform`, and `X86BcjTransform` for `PE`/`ELF` executables.
- **`.ucx` Multi-File Archive Container & Resilient Queue:** Content-addressed multi-block `.ucx` archives with folder hierarchy, SHA-256 exact-duplicate file deduplication, per-class solid/store blocks, O(1) manifest browsing (`list`), selective extraction (`extract --select`), cooperative pause/resume/cancel, destination pre-flight checks, and SQLite `jobs.db` crash recovery.
- **WinUI 3 Desktop Archive Manager & Optimization Studio:** Expanded desktop workspace with drag-and-drop batch `.ucx` archiving, Advisor rationale cards, interactive `.ucx` browser, before/after comparison, duplicate badges, compression history, actionable error remediation, and the consent-gated Optimization & Data Hygiene Studio.
- **Reproducible Competitor Benchmark Suite & Interactive Explorer:** Head-to-head benchmark data and interactive explorer ([benchmarks.html](https://windfastt.github.io/ultracompress/benchmarks.html)) comparing UltraCompress against `7-Zip`, `Zstandard`, `Brotli`, and `GZip`/`LibDeflate` across 6 workload categories and 6 efficiency metrics (`CPU-sec / GiB saved`, Peak RAM, compress/decompress `MB/s`, CPU%, ratio).
- **Multi-Backend Engine & Fleet Integrations (Merged PRs #1–#3, #5–#6, #8–#16):** `ZstdSharp`, `7za` (`LZMA2`), `Brotli`, and `LibDeflate` worker backends, SQLite `recipes.db` store, UCB1 tournament coordinator, gRPC distributed LAN worker runtime (`17833`), Explorer context-menu enqueue client, golden corpus Windows CI, and WiX installer bundle (`UltraCompress-Setup.exe` / `UltraCompress.msi`).

### Changed

- Overhauled product site (`docs/site/`), `README.md`, `docs/USER.md`, and developer documentation to present clear competitive positioning, canonical 10 GB workload routing, strict lossless profile guarantees, and four-mode data safety UX.
- Removed legacy suggestions of lossy behavior inside `Laboratory` compression profiles; `Laboratory` is strictly bit-exact lossless.

## [0.3.1] - 2026-11-24

### Added

- WinUI 3 system tray with job queue UI and profile picker
- `UltraCompress.Engine` Windows Service with named-pipe IPC and Serilog rolling file logging
- `UltraCompress.Worker` CLI with Zstandard round-trip verification
- Core recipe JSON schemas, profile defaults, and unit test suite
- BenchmarkDotNet harness and `windows-latest` CI workflow
- Initial GitHub Pages site and tag-driven GitHub Release workflow
- MIT license and contributor documentation

## [0.2.0] - 2026-11-24

### Added

- Initial `.NET 8` solution layout (`UltraCompress.Core`, `UltraCompress.Engine`, `UltraCompress.Worker`, `UltraCompress.Tray`) and test projects
