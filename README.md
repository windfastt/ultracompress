# UltraCompress

[![CI](https://github.com/windfastt/ultracompress/actions/workflows/ci.yml/badge.svg)](https://github.com/windfastt/ultracompress/actions/workflows/ci.yml)
[![Pages](https://github.com/windfastt/ultracompress/actions/workflows/pages.yml/badge.svg)](https://windfastt.github.io/ultracompress/)
[![MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![v1.0.0](https://img.shields.io/badge/release-v1.0.0--rc-2dd4bf)

**UltraCompress** is a Windows-first **content-aware archive engine and WinUI 3 desktop archive manager** that inspects every stream in O(1) memory, routes each file class to its optimal lossless transform + codec pipeline, collapses exact duplicates via SHA-256 content addressing, and explains every compression decision.

**Product Site:** [windfastt.github.io/ultracompress](https://windfastt.github.io/ultracompress/) · **Live Benchmark Explorer:** [windfastt.github.io/ultracompress/benchmarks.html](https://windfastt.github.io/ultracompress/benchmarks.html)  
**Documentation:** [Profiles & Safety](docs/profiles-and-safety.md) · [User Guide](docs/USER.md) · [Roadmap](docs/release-roadmap.md) · [Desktop Architecture](docs/dev/tray-winui.md) · [Advisor & Inspector](docs/dev/advisor-v1.md) · [Distributed Workers](docs/DISTRIBUTED_WORKERS.md)

---

## Why UltraCompress Exists

Generic archivers (`7-Zip`, `WinRAR`, `PeaZip`, `Zstandard`, `gzip`) apply **one blind codec and compression level uniformly** across an entire job. On real-world mixed folders, uniform compression fails in two opposite ways:

1. **Wasted CPU & Hours on Incompressible Media:** Running `7-Zip LZMA2 -mx=9` or `zstd -19` on `MP4`, `MKV`, `JPEG`, or `HEIC` streams burns 100% CPU for 30–45 minutes per 10 GB to achieve `< 0.15%` savings.
2. **20–50% Missed Lossless Savings on Structured & Duplicate Data:** Applying raw general-purpose codecs without per-stream `ByteShuffle` / `Delta` / `X86Bcj` preprocessors, extension-sorted solid blocks, or SHA-256 file deduplication leaves gigabytes of recoverable redundancy inside database dumps, telemetry CSVs, uncompressed rasters, native binaries, and monorepos.
3. **Cloud Storage Bloat:** Uploading uninspected project directories to S3/Blob/Drive bills recurring storage and egress on duplicate build artifacts and untransformed structured dumps.

**How UltraCompress solves this:**
- **5-Window O(1)-RAM `ContentInspector`:** Samples `head`, `25%`, `50%`, `75%`, and `tail` windows (`<= 1 MiB` constant probe memory even on `100 GB` inputs) to detect 30+ magic/container formats, Shannon entropy (`bits/byte`), bigram predictability, and 4/8-byte stride correlations before launching an encoder.
- **Heterogeneous `.ucx` Multi-File Container:** Within a single `.ucx` archive, already-compressed media streams bypass heavy codecs at NVMe speed (`FastStreamBypass`), source files pack into type-sorted solid dictionary blocks with `< 45 ms` selective extraction, structured numeric/DB streams pass through reversible `ByteShuffle` + `Delta` transforms, `PE`/`ELF` binaries use `X86Bcj` jump filtering, and identical files collapse to zero extra bytes via SHA-256 content addressing.
- **Verified Multi-Candidate UCB1 Tournaments (`Tiny` & `Laboratory`):** Pilots candidate transform + codec pipelines (`Zstd`, `7za LZMA2`, `Brotli`, `LibDeflate`) on sample chunks within strict time/RAM caps, selects the empirically smallest verified output, and caches winning recipes in SQLite (`recipes.db`).
- **Explainable `StrategyDecisionReport`:** Every Desktop job card and `uc analyze --explain` run reports the exact inspection metrics, selected pipeline, rejected codecs, and verified byte savings.

---

## Canonical 10 GB Workload Outcomes

| 10 GB Workload | `ContentInspector` Signals | Selected Lossless Strategy | Verified Outcome (vs Uniform `7-Zip -mx=9`) |
|----------------|----------------------------|----------------------------|---------------------------------------------|
| **10 GB Video (`MP4`/`MKV`)** | `ftypisom` / Matroska magic · `7.98 bpb` entropy across all 5 windows | `FastStreamBypass (Store)` + streaming SHA-256/XXH64 seal | **`4.8 s` (`2,130 MB/s`)** vs `41m 18s` in `7-Zip` (`99.8%` less CPU, zero quality change) |
| **10 GB Source Repository** | `4.52 bpb` entropy · high token repetition · `1.84 GB` duplicate files | SHA-256 dedup + extension-sorted `64 MiB` solid blocks + long-window `Zstd-15` | **`10.00 GB → 1.12 GB` (`88.8%` saved)** · `< 45 ms` single-file selective extract |
| **10 GB Database Dump (`SQL`/`SQLite`/`CSV`)** | 8-byte (`r = 0.74`) & 4-byte (`r = 0.68`) stride correlation · page headers | `ByteShuffle(8)` + `Delta` lossless preprocessors + `Zstd-19` tournament winner | **`10.00 GB → 1.54 GB` (`84.6%` saved)** · `38%` smaller than raw `Zstd-19`, `3.1x` faster than `7-Zip` |
| **10 GB Photo Collection (`JPEG` + `RAW`/`TIFF` + Dupes)** | Per-file split: `7.95 bpb` `JPEG`/`HEIC` vs `5.41 bpb` 3-ch `RAW`/`BMP` + `0.70 GB` dupes | Heterogeneous `.ucx`: `Store` for `JPEG`/`HEIC`, `Delta+Zstd` for `RAW`/`TIFF`, SHA-256 dedup | **`10.00 GB → 7.58 GB` (`2.42 GB` saved losslessly)** in **`19.4 s`** (zero pixel/EXIF mutation) |

Explore full competitor tables across 6 categories (`7-Zip`, `Zstandard`, `Brotli`, `GZip`/`LibDeflate`) on the [Interactive Benchmark Explorer](https://windfastt.github.io/ultracompress/benchmarks.html).

---

## Five 100% Bit-Exact Lossless Profiles

Every compression profile is **strictly bit-exact lossless**, verified by per-block `XXH64` and end-to-end `SHA-256` checksums. Lossy transforms are banned from all compression profiles.

| Profile | Throughput Floor | Peak RAM Cap | Entropy Bypass Gate | Strategy & Search Budget |
|---------|------------------|--------------|---------------------|--------------------------|
| **`Instant`** | `>= 650 MB/s` | `128 MiB` | `>= 7.65 bpb` | Single fast arm (`LibDeflate` / `Zstd-1`) + SHA-256 dedup |
| **`Balanced`** *(Default)* | `>= 280 MB/s` | `256 MiB` | `>= 7.85 bpb` | Advisor single-winner (`Zstd-5` + `Delta`/`ByteShuffle`/`X86Bcj` + `32 MiB` solid blocks) |
| **`Archive`** | `>= 85 MB/s` | `768 MiB` | `>= 7.92 bpb` | Long-window `Zstd-15` / `Brotli-9` + `64 MiB` class-sorted solid blocks |
| **`Tiny`** | `>= 22 MB/s` | `1,536 MiB` | `>= 7.95 bpb` | 4-arm chunk pilot + UCB1 tournament (`60 s` search cap) + SQLite recipe cache |
| **`Laboratory`** | `>= 6 MB/s` | `4,096 MiB` | `>= 7.98 bpb` | 8-arm exhaustive UCB1 tournament (`LZMA2 -mx=9`, `Zstd-22 --ultra`, chained transforms · `300 s` cap) |

---

## Strict Four-Mode Data Safety Guarantee

UltraCompress never mixes lossless archival with destructive mutations. Four distinct `OperationKind` modes are enforced across Core contracts, Engine IPC, CLI, and Desktop UI (see [docs/profiles-and-safety.md](docs/profiles-and-safety.md)):

1. **`LosslessCompression` (Default):** Bit-exact restoration guaranteed across all 5 profiles. Zero pixel re-encoding, zero EXIF/metadata stripping.
2. **`LossyOptimization` (Consent-Gated Studio):** Separate workflow for perceptual media size reduction (`WebP`/`AVIF`/bitrate targets). Requires explicit `ConsentConfirmation`, side-by-side visual/SSIM fidelity preview, and preserves original files untouched by default (`PreserveOriginal = true`).
3. **`FormatConversion` (Explicit Transcoding):** Deterministic container/format migration (`BMP` → lossless `PNG`, `CSV` → `Parquet`, `.zip` → `.ucx`) with schema/extension diff preview and original preservation by default.
4. **`DataRemoval` (Metadata & Artifact Hygiene):** Explicit stripping of GPS/camera EXIF tags, debug symbols (`PDB`), or build caches with an itemized byte-level manifest, mandatory consent gate, and original preservation by default.

---

## Product Surfaces

- **UltraCompress Desktop & Tray (`UltraCompress.Tray` — WinUI 3):** Full desktop archive manager featuring drag-and-drop file/folder batch `.ucx` archiving, live `StrategyDecisionReport` rationale cards, interactive `.ucx` archive browser, selective file/glob extraction, duplicate detection badges, before/after visual size breakdown, cooperative pause/resume/cancel, SQLite crash recovery, destination free-space pre-flight checks, searchable compression history, actionable error remediation, and the isolated **Optimization & Data Hygiene Studio**.
- **Background Engine Service (`UltraCompress.Engine`):** Windows Service (`\\.\pipe\UltraCompress.Engine` named-pipe IPC + localhost gRPC `17832` + optional LAN worker fleet on `17833`), persistent `jobs.db` crash-recovery journal, atomic `.part` staging, and out-of-process worker isolation.
- **Worker & CLI (`UltraCompress.Worker` / `uc`):** Full command-line parity for `pack`/`compress`, `analyze --explain`, `list`, `extract --select`, `verify`, `history`, and `benchmark`.
- **Explorer Integration & Installer (`UltraCompress.Shell` + `installer/wix`):** Windows 11 Explorer context-menu enqueue integration and WiX bundle (`UltraCompress-Setup.exe` / `UltraCompress.msi`) packaging Desktop, Engine, Worker CLI, and third-party license notices.

---

## Quick Start

### Build & Test from Source (.NET 8)

```powershell
dotnet build src/UltraCompress.sln -c Release
dotnet test src/UltraCompress.sln -c Release --no-build
```

### Build Windows Installer Bundle (WiX)

```powershell
dotnet build installer/wix/UltraCompress.Bundle/UltraCompress.Bundle.wixproj -c Release
# Outputs installer/wix/UltraCompress.Bundle/bin/Release/UltraCompress-Setup.exe
```

### CLI Examples

```powershell
# Inspect a file or folder and print the full StrategyDecisionReport without writing output
dotnet run -c Release --project src/UltraCompress.Worker -- analyze --input .\data\analytics.db --profile Tiny --explain

# Compress a single file or multi-file folder with verified round-trip integrity
dotnet run -c Release --project src/UltraCompress.Worker -- compress --input .\repo-snapshot --profile Balanced

# Run reproducible benchmarks
dotnet run -c Release --project tests/UltraCompress.Benchmarks -- --ci
```

---

## License & Third-Party Notices

Licensed under the [MIT License](LICENSE). Bundled/invoked codecs (`Zstandard` BSD, `Brotli` MIT, `libdeflate` MIT, and isolated `7za` subprocess under LGPLv2.1+) are documented in [docs/legal/THIRD_PARTY_NOTICES.md](docs/legal/THIRD_PARTY_NOTICES.md) and [tools/7z/README.md](tools/7z/README.md). Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) and [CHANGELOG.md](CHANGELOG.md).
