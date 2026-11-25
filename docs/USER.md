# UltraCompress — User Guide

UltraCompress is a content-aware archive manager and compression engine for Windows 10/11. Instead of forcing one blind codec across an entire folder, UltraCompress inspects every stream in O(1) memory, routes each content class to its optimal lossless pipeline inside an indexed `.ucx` archive, deduplicates identical files via SHA-256, and explains every decision.

For full SLA tables and the four-mode data safety boundary, see **[Profiles & Safety Specification](profiles-and-safety.md)**.

---

## 1. Choosing a Lossless Profile

All five compression profiles are **100% bit-exact lossless** and verified by `XXH64` + `SHA-256` checksums:

| Profile | Best For | Throughput Floor | Peak RAM Cap | Behavior |
|---------|----------|------------------|--------------|----------|
| **`Instant`** | Scratch backups, CI build caches, fast local staging | `>= 650 MB/s` | `128 MiB` | `LibDeflate` / `Zstd-1` + SHA-256 exact-file dedup; bypasses streams `>= 7.65 bpb` |
| **`Balanced`** *(Default)* | Daily project archiving, mixed media & code folders | `>= 280 MB/s` | `256 MiB` | Advisor single-winner: `Zstd-5`, `Delta`/`ByteShuffle`/`X86Bcj` per class, `32 MiB` solid blocks |
| **`Archive`** | Long-term project snapshots, database backups | `>= 85 MB/s` | `768 MiB` | Long-window `Zstd-15` / `Brotli-9` + extension-sorted `64 MiB` solid dictionary blocks |
| **`Tiny`** | Network transfers, release artifacts, tight storage | `>= 22 MB/s` | `1,536 MiB` | 4-arm chunk pilot + UCB1 tournament (`60 s` search cap); caches winning recipe in SQLite |
| **`Laboratory`** | Permanent cold masters, benchmark ceiling | `>= 6 MB/s` | `4,096 MiB` | 8-arm exhaustive UCB1 tournament (`LZMA2 -mx=9`, `Zstd-22 --ultra`, chained transforms · `300 s` cap); **strictly lossless** |

*(Legacy CLI profile names `Small` and `Experimental` remain accepted as compatibility aliases for `Archive` and `Laboratory`.)*

---

## 2. Desktop Archive Manager & System Tray

Launch **UltraCompress Desktop** from the Start Menu or system tray icon:

- **Drag & Drop Archiving:** Drop files or multi-gigabyte folders onto the workspace to preview `ContentInspector` signals, exact duplicate savings, before/after byte estimates, and the `StrategyDecisionReport` rationale card before starting.
- **`.ucx` Archive Browser & Selective Extraction:** Open any `.ucx` archive to browse its folder tree in O(1) manifest read time, inspect per-file codecs and SHA-256 digests, verify archive health, or selectively extract individual files and subfolders.
- **Queue Control & Crash Recovery:** Pause, resume, or cancel running jobs at chunk boundaries. Jobs stage atomically to `.part` files and journal progress in `%ProgramData%\UltraCompress\jobs.db`, automatically recovering interrupted work after a reboot or power outage.
- **Destination Manager:** Pre-flight free-space checks prevent mid-archive disk exhaustion; configurable conflict rules handle existing output paths safely.
- **Compression History:** Browse historical jobs, cumulative GiB and CPU-hours saved, and cached SQLite recipes (`recipes.db`).
- **Optimization & Data Hygiene Studio:** Explicitly separated workspace for consent-gated **Lossy Optimization** (with side-by-side visual/SSIM preview), **Format Conversion**, and **Data Removal** (with itemized EXIF/symbol manifest). Original files are preserved untouched by default.

---

## 3. CLI Quick Start

```powershell
# Compress a file or directory using the default Balanced profile
dotnet run -c Release --project src/UltraCompress.Worker -- compress --input "C:\Projects\AppRepo" --profile Balanced

# Inspect a workload and explain why codecs/preprocessors were selected or rejected
dotnet run -c Release --project src/UltraCompress.Worker -- analyze --input "C:\Backups\events.db" --profile Tiny --explain

# Browse entries or selectively extract matching files from a .ucx archive
uc list "C:\Archives\AppRepo.ucx"
uc extract "C:\Archives\AppRepo.ucx" --select "src/**/*.cs" --output "C:\Restore"

# Verify cryptographic SHA-256 & XXH64 archive integrity
uc verify "C:\Archives\AppRepo.ucx"
```

---

## 4. File Explorer Integration

Right-click files or folders in Windows Explorer to enqueue jobs directly into the running `UltraCompress.Engine` service via `UltraCompress.Shell`. Registering the Windows 11 sparse-package context menu on end-user machines requires an Authenticode-signed package — see [SHELL_EXTENSION.md](./SHELL_EXTENSION.md) and [OPERATOR_SIGNING.md](./OPERATOR_SIGNING.md).

---

## 5. Worker Isolation & Safety

All compression, preprocessing, and decompression codecs execute in isolated worker processes managed by `UltraCompress.Engine`. Even on multi-gigabyte `Laboratory` archives, UI responsiveness is unaffected, worker memory stays bounded by the active `ProfileContract`, and outputs are finalized only after checksum verification passes.
