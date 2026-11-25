# UltraCompress Product & Release Roadmap

Canonical repository: [github.com/windfastt/ultracompress](https://github.com/windfastt/ultracompress)  
Product site: [windfastt.github.io/ultracompress](https://windfastt.github.io/ultracompress/)  
Benchmark explorer: [windfastt.github.io/ultracompress/benchmarks.html](https://windfastt.github.io/ultracompress/benchmarks.html)  
Safety & profile specification: [docs/profiles-and-safety.md](profiles-and-safety.md)

---

## Completed Foundation (`main` @ `262832f`)

All foundational engine, codec, IPC, tournament, shell, installer, and CI stabilization slices (PRs #1–#3, #5–#6, #8–#16, #18) are merged and green on `main`:

- Core pipeline schema (`PipelineRecipe`, `FileFingerprint`), JSON schema validation, and SQLite recipe store (`recipes.db`)
- Multi-codec execution backends: `ZstdSharp` (`zstd`), isolated `7za` (`LZMA2`), `.NET Brotli`, and `LibDeflate` with round-trip golden corpus CI on Windows
- `UltraCompress.Engine` Windows Service with named-pipe IPC (`\\.\pipe\UltraCompress.Engine`), localhost gRPC (`17832`), and optional token-authenticated LAN worker fleet (`17833`)
- Multi-candidate UCB1 tournament search coordinator, Advisor decision framework, WinUI 3 desktop/tray host, Explorer context-menu enqueue client, and WiX installer bundle (`UltraCompress.msi` + `UltraCompress-Setup.exe`)

---

## Active Release Milestone: v1.0.0 Commercial-Grade Readiness

Version `1.0.0` completes the end-to-end desktop archive manager, content-aware `.ucx` multi-file container, strict four-mode data safety boundary, and reproducible competitor benchmark suite with zero architectural stubs.

### Track 1: Measurable Lossless Profiles & Four-Mode Data Safety
- Enforce **100% bit-exact lossless** semantics verified by `XXH64` + `SHA-256` across all five compression profiles (`Instant`, `Balanced`, `Archive`, `Tiny`, `Laboratory`).
- Publish and enforce explicit `ProfileContract` SLAs (throughput floor, peak worker RAM cap, high-entropy bypass gate, candidate pilot budget).
- Ship reversible lossless preprocessors (`DeltaTransform`, `ByteShuffleTransform`, `X86BcjTransform`) and empirical chunk pilot + UCB1 tournament execution for `Tiny` and `Laboratory`.
- Enforce strict `OperationKind` separation: `LosslessCompression`, `LossyOptimization`, `FormatConversion`, and `DataRemoval` with mandatory `ConsentConfirmation`, fidelity/impact preview metadata, and `PreserveOriginal = true` by default.

### Track 2: Intelligent File Identification & Explainable Strategy Selection
- Multi-window O(1)-memory `ContentInspector` (`head`, `25%`, `50%`, `75%`, `tail`) identifying 30+ container and magic signatures (`MP4`/`MKV`, `JPEG`/`PNG`/`HEIC`/`RAW`/`BMP`/`TIFF`, `SQLite`/`SQL`/`JSON`/`CSV`/`Parquet`, `PE`/`ELF`, archives, source trees), Shannon entropy, bigram predictability, and 4/8-byte stride correlation.
- Proven per-stream routing across the four canonical 10 GB workloads: high-entropy video bypass, solid + deduplicated source repository, byte-shuffle/delta database dump, and heterogeneous `JPEG`-store vs `RAW`-compress photo collection.
- Human-readable and JSON `StrategyDecisionReport` surfaced in `analyze --explain` and Desktop job rationale cards.

### Track 3: `.ucx` Multi-File Archive Container & Resilient Queue Engine
- Content-addressed `.ucx` v1 multi-file archive reader/writer preserving directory hierarchy, per-class solid/store blocks, SHA-256 exact-duplicate file deduplication, O(1) indexed manifest listing (`list`), and selective file/glob extraction (`extract --select`) with path-traversal protection.
- Persistent SQLite job journal (`jobs.db`) with atomic `.part` staging and automatic **crash recovery** on service or app restart.
- Cooperative chunk-level **Pause / Resume / Cancel**, **Destination Management** (conflict policies + pre-flight free-space checks), persistent **Compression History**, and actionable error remediation.

### Track 4: Full WinUI 3 Desktop Archive Manager
- Full **UltraCompress Desktop** workspace + system tray companion:
  - Drag-and-drop file/folder batch `.ucx` archiving with live Advisor rationale card, duplicate detection badge, before/after size comparison, and destination manager.
  - Interactive `.ucx` Archive Browser & Selective Extractor with tree search and one-click SHA-256 integrity check.
  - Queue & Crash Recovery center with live MB/s, ETA, pause/resume/cancel, and plain-language error remediation.
  - Searchable Compression History & cumulative storage/CPU savings ledger.
  - Isolated **Optimization & Data Hygiene Studio** for consent-gated `LossyOptimization`, `FormatConversion`, and `DataRemoval` with locked-by-default original file preservation.

### Track 5: Reproducible Competitor Benchmark Suite
- Same-hardware benchmark harness comparing UltraCompress against `7-Zip` (`7za`), `Zstandard` (`zstd`), `Brotli`, and `GZip`/`LibDeflate` across 6 deterministic workload categories (mixed workspace, 4K video, source repository, database dump, photo collection, native binaries).
- Publishes compression ratio, compression MB/s, decompression MB/s, CPU utilization/time, Peak Working Set RAM, and CPU-seconds per GiB saved alongside hardware manifests and CLI reproduction scripts.

### Track 6: Product Positioning, Safety Documentation & Interactive Site
- Comprehensive [Profiles & Safety Specification](profiles-and-safety.md), updated [User Guide](USER.md), and SaaS/Desktop product site at [windfastt.github.io/ultracompress](https://windfastt.github.io/ultracompress/).
- Interactive 4-workload `ContentInspector` showcase, 4-mode Data Safety UX walkthrough, Desktop Archive Manager tour, and dynamic [Competitor Benchmark Explorer](https://windfastt.github.io/ultracompress/benchmarks.html) backed by machine-readable JSON artifacts.
- Tagged GitHub Release (`v1.0.0`) publishing `UltraCompress-Setup.exe`, `UltraCompress.msi`, standalone CLI binaries, and benchmark artifacts.

---

## Post-v1.0 Horizon (v1.1+)

- **Authenticode Hardware Token Signing Automation:** EV certificate signing in release pipeline for zero-warning SmartScreen reputation and automatic Windows 11 sparse-package Explorer context registration (see [OPERATOR_SIGNING.md](OPERATOR_SIGNING.md)).
- **UltraCompress Cloud Fleet (Optional Managed Sync):** Shared team SQLite recipe catalogs and remote cloud worker pools using the existing gRPC `WorkerService` protocol.
- **Chunk-Level Cross-File CDC Deduplication:** Rolling Rabin/FastCDC content-defined chunking for near-duplicate VM images and incremental database backups.
