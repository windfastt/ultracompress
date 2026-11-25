# UltraCompress Competitor Benchmark Report & Methodology

This document publishes **live, machine-measured, bit-exact SHA-256 verified** compression benchmarks comparing **UltraCompress** (`Instant`, `Balanced`, `Archive`, `Tiny`) head-to-head against **7-Zip (`7za`)**, **Zstandard (`zstd`)**, **Brotli (`brotli`)**, **GNU GZip (`gzip`)**, and **LibDeflate (`libdeflate-gzip`)** on identical hardware and deterministic multi-category datasets.

- **Machine-readable JSON artifact:** [`benchmarks/results/latest-benchmark-report.json`](../benchmarks/results/latest-benchmark-report.json)
- **Machine-readable CSV artifact:** [`benchmarks/results/latest-benchmark-report.csv`](../benchmarks/results/latest-benchmark-report.csv)
- **Benchmark harness source:** [`benchmarks/UltraCompress.Benchmarks/`](../benchmarks/UltraCompress.Benchmarks/)
- **Determinism & round-trip test suite:** [`benchmarks/UltraCompress.Benchmarks.Tests/`](../benchmarks/UltraCompress.Benchmarks.Tests/)
- **Continuous verification workflow:** [`.github/workflows/benchmarks.yml`](../.github/workflows/benchmarks.yml)

---

## 1. One-Command Reproduction

Every byte of the benchmark corpus is synthesized deterministically via a cross-platform `SplitMix64` + `Xoshiro256**` PRNG (`DeterministicRng`) with fixed category seeds and zeroed USTAR metadata. Regenerating the corpus on Linux, Windows, or macOS produces the **exact same per-file and aggregate SHA-256 digests**.

### Linux / macOS / WSL (Bash)

```bash
# Prerequisites (Ubuntu/Debian):
sudo apt-get update && sudo apt-get install -y 7zip p7zip-full zstd brotli gzip libdeflate-tools time

# Full Standard suite (3 measured iterations per pair, median reported, SHA-256 determinism verified):
./scripts/run-benchmarks.sh --scale Standard --warmup 0 --iterations 3 --verify-determinism --require-all-competitors

# Fast CI smoke verification (~25 seconds):
./scripts/run-benchmarks.sh --smoke --verify-determinism --require-all-competitors

# Regenerate deterministic corpus standalone and inspect SHA-256 manifests:
./scripts/run-benchmarks.sh --generate-corpus /tmp/uc-corpus --scale Standard
```

### Windows 10 / 11 (PowerShell)

```powershell
# Optional competitor CLIs via Chocolatey / Winget:
choco install 7zip zstandard -y

# Full Standard suite:
pwsh ./scripts/run-benchmarks.ps1 -Scale Standard -VerifyDeterminism

# Fast CI smoke verification:
pwsh ./scripts/run-benchmarks.ps1 -Smoke -VerifyDeterminism
```

---

## 2. Testbed Hardware, OS & Tool Versions

All 78 `(Category, Tool/Profile)` rows (234 total compress + decompress + SHA-256 verification runs at `3` iterations per pair) were executed on the same host:

| Specification | Measured Value |
|---|---|
| **Measured Timestamp** | `2026-11-24 19:44 EST (UTC-5)` |
| **CPU** | `Intel(R) Xeon(R) Processor` (`GenuineIntel`, Family 6 Model 207 / Emerald Rapids) |
| **Topology** | `4 Physical Cores / 4 Logical vCPUs` (NUMA node 0) |
| **Instruction Sets** | `SSE4.2, AVX, AVX2, AVX-512F, AVX-512BW, AVX-512VL, BMI2, SHA-NI, VAES` |
| **Available System Memory** | `15 GiB RAM` (`11,234.4 MiB` GC-available budget, `0 B` swap) |
| **Operating System & Kernel** | `Ubuntu 24.04.5 LTS` · `Linux 6.12.113+ x86_64` |
| **.NET Runtime & SDK** | `.NET 8.0.34` (`SDK 8.0.428`, `Release` build, TieredPGO enabled) |

### Benchmarked Tools & Exact Flags

| Tool / Profile | Exact Captured Version | Compress Command / Pipeline | Decompress Command | Mode |
|---|---|---|---|---|
| **UltraCompress (`Instant`)** | `UltraCompress 0.3.1` | `uc pack --profile Instant` (smart media bypass + SHA-256 dedup + solid `zstd-1`) | `uc extract --verify-sha256` | Managed In-Process |
| **UltraCompress (`Balanced`)** | `UltraCompress 0.3.1` | `uc pack --profile Balanced` (content-routed bypass + dedup + `ByteShuffle`/`Delta`/`X86Bcj` + solid `zstd-6`) | `uc extract --verify-sha256` | Managed In-Process |
| **UltraCompress (`Archive`)** | `UltraCompress 0.3.1` | `uc pack --profile Archive` (per-class solid + transforms + `brotli-9` text / `zstd-15` binary) | `uc extract --verify-sha256` | Managed In-Process |
| **UltraCompress (`Tiny`)** | `UltraCompress 0.3.1` | `uc pack --profile Tiny` (`UCB1` 64 KiB pilot + parallel tournament: transforms × `{Store, zstd-22, brotli-11, libdeflate-12, 7z-mx9}`) | `uc extract --verify-sha256` | Managed + `7za` Arm |
| **7-Zip (`-mx=5`)** | `7-Zip (a) 23.01 (x64)` | `7za a -t7z -m0=LZMA2 -mx=5 -ms=on -mmt=on` | `7za x -y` | External Child CLI |
| **7-Zip (`-mx=9`)** | `7-Zip (a) 23.01 (x64)` | `7za a -t7z -m0=LZMA2 -mx=9 -ms=on -mmt=on` | `7za x -y` | External Child CLI |
| **Zstandard (`-3`)** | `Zstandard CLI v1.5.5 (64-bit)` | `zstd -3 -f -q <category.tar> -o <archive.tar.zst>` | `zstd -d -f -q` + untar | External Child CLI |
| **Zstandard (`-19`)** | `Zstandard CLI v1.5.5 (64-bit)` | `zstd -19 -f -q <category.tar> -o <archive.tar.zst>` | `zstd -d -f -q` + untar | External Child CLI |
| **Brotli (`-q 6`)** | `brotli 1.1.0` | `brotli -q 6 -f <category.tar> -o <archive.tar.br>` | `brotli -d -f` + untar | External Child CLI |
| **Brotli (`-q 11`)** | `brotli 1.1.0` | `brotli -q 11 -f <category.tar> -o <archive.tar.br>` | `brotli -d -f` + untar | External Child CLI |
| **GZip (`-6`)** | `gzip 1.12` | `gzip -6 -k -f <category.tar>` | `gzip -d -k -f` + untar | External Child CLI |
| **LibDeflate (`-6`)** | `libdeflate-gzip v1.19` | `libdeflate-gzip -6 -k -f <category.tar>` | `libdeflate-gzip -d -k -f` + untar | External Child CLI |
| **LibDeflate (`-12`)** | `libdeflate-gzip v1.19` | `libdeflate-gzip -12 -k -f <category.tar>` | `libdeflate-gzip -d -k -f` + untar | External Child CLI |

---

## 3. Deterministic 6-Category Corpus Specification

Aggregate `Standard` Corpus SHA-256: `dce7e7f1c2a4ad9adbde5530cd59e0e5a76d60fefb31236b63aaf589c1343d2e`

| Category | Seed | Files (Dupes) | Raw Size | Mean Entropy | Structural Composition & Real-World Archetype | Manifest SHA-256 (Prefix) |
|---|---:|---:|---:|---:|---|---|
| `video-stream` | `0x554C5452410001` | `2 (0)` | `4.40 MiB` | `8.000 bpb` | ISO-BMFF `MP4` container boxes (`ftypisom`, `moov`, `mvhd`, `trak`) + H.264 NAL start codes + CABAC-like high-entropy frames | `45970324e5fb4c72…` |
| `source-repo` | `0x554C5452410002` | `112 (16)` | `1.21 MiB` | `4.951 bpb` | C# services/tests, React `.tsx` components, `package-lock.json`, configs, Markdown RFCs + 3× duplicated vendored SDK files | `d8657664c7d6fc9a…` |
| `database-dump` | `0x554C5452410003` | `4 (0)` | `4.40 MiB` | `4.750 bpb` | SQL `CREATE`/`INSERT` dump + 9-column settlement CSV + 8-byte columnar & 32-byte row binary pages with monotonic/delta numeric correlation | `ec98be616d663505…` |
| `photo-collection` | `0x554C5452410004` | `13 (3)` | `7.70 MiB` | `7.546 bpb` | 6 high-entropy `JPEG` camera files (`~7.98 bpb`), 2 uncompressed 24-bit `BMP` plates, 2 uncompressed 32-bit RGBA `TIFF` scans, 3 duplicate burst picks | `7c67d89ea93a8983…` |
| `native-binaries` | `0x554C5452410005` | `4 (0)` | `4.40 MiB` | `5.753 bpb` | 2 Windows x64 `PE` binaries (`.dll`/`.exe`) + 2 Linux x64 `ELF` binaries (`.so`/daemon) with `E8`/`E9` `CALL`/`JMP rel32` code sections & UTF-16LE symbol/resource tables | `56c0a81db5668536…` |
| `mixed-workspace` | `0x554C5452410006` | `66 (9)` | `7.74 MiB` | `6.544 bpb` | Combined workspace merging `media/`, `repo/`, `database/`, `photos/`, and `binaries/` subtrees into one heterogeneous folder | `3aa8c35071f120b6…` |

### Apples-to-Apples Multi-File Fairness Protocol

1. **Identical Payload Accounting (`OriginalBytes`)**: For every tool, `OriginalBytes` is the exact sum of raw file sizes in `corpus/files/<category>/`, and `CompressedBytes` is the exact on-disk archive byte length (`.ucx`, `.7z`, `.tar.zst`, `.tar.br`, `.tar.gz`).
2. **Solid Context Granted to Single-Stream Competitors**: Because `zstd`, `brotli`, `gzip`, and `libdeflate-gzip` compress single byte streams rather than directory trees, `CorpusBuilder` pre-stages a canonical, zero-mtime POSIX USTAR `.tar` before timing starts. Stream competitors compress `<category>.tar` directly in native C (giving them full cross-file solid dictionary context without charging `tar` creation time to their compression clock) and decompress back to `.tar`, which the harness unpacks and verifies file-by-file via SHA-256.
3. **Mandatory Bit-Exact SHA-256 Restoration**: Every single trial extracts all files into a fresh empty directory and recomputes the sorted `(relativePath, byteLength, fileSha256)` manifest digest. Any mismatch aborts the benchmark immediately. All 234 trials passed 100% bit-exact verification.

---

## 4. Metric Definitions & Energy Model

- **`CompressionRatio`**: `OriginalBytes / CompressedBytes` (higher is smaller output; `1.000x` = unchanged size).
- **`SavingsPercent`**: `(1 - CompressedBytes / OriginalBytes) * 100%`. Slightly negative values (e.g., `-0.01%` to `-0.05%` on `video-stream`) reflect archive/frame headers added around incompressible H.264 payloads.
- **`CompressThroughputMBps` / `DecompressThroughputMBps`**: `(OriginalBytes / 1,048,576) / WallSeconds` (`MiB/s`), reporting the median across 3 measured runs after a suite-level JIT pre-warm.
- **`CpuUserMs`, `CpuKernelMs`, `CpuTotalMs`**: Microsecond-precision kernel accounting via Linux `getrusage(RUSAGE_SELF)` / `getrusage(RUSAGE_CHILDREN)` (and Win32 `GetProcessTimes` on Windows). When `UltraCompress (Tiny)` invokes an isolated `7za` LZMA2 candidate arm, child `7za` CPU time is summed into `UltraCompress`'s CPU total so subprocess work is never hidden.
- **`CpuUtilizationPercent`**: `(CpuTotalMs / CompressWallMs) * 100%` (`100%` = 1 logical vCPU saturated; up to `400%` on this 4-vCPU host). `CpuSystemUtilizationPercent` (`0–100%` of total host capacity) is additionally recorded in the JSON and CSV artifacts.
- **`AvgRamMB` & `PeakWorkingSetMB`**:
  - *External CLI processes*: Kernel `ru_maxrss` captured via `/usr/bin/time -v` (`Maximum resident set size`) paired with `/proc/<pid>/statm` sampling (`Process.PeakWorkingSet64` / `WorkingSet64` on Windows).
  - *Managed In-Process (`UltraCompress`)*: Live `.NET 8` runtime RSS baseline + active managed/native codec working-set growth + child `7za` RSS (when `Tiny` evaluates LZMA2), after compacting LOH GC and `malloc_trim(0)` between runs so trials do not inherit stale arenas from prior categories.
- **`CpuSecondsPerGiBSaved`**: `(CpuTotalMs / 1000) / ((OriginalBytes - CompressedBytes) / 2^30)`. Measures compute cost per GiB of storage saved (lower is more efficient; `N/A` on incompressible `video-stream` where `SavedBytes <= 0`).
- **`EnergyMilliwattHours` (CPU-Energy Model)**: Cloud VM guests do not expose bare-metal Intel RAPL (`/sys/class/powercap/intel-rapl:0/energy_uj`). When RAPL is unavailable, the harness transparently applies a linear CPU-time TDP model using a documented **`15.0 W / logical vCPU`** reference (`60.0 W` 4-vCPU Xeon slice):
  $$\text{Energy}_{\text{mWh}} = \frac{(\text{CpuTotalMs} / 1000) \times 15.0\text{ W}}{3.6\text{ J/mWh}}$$
  On bare-metal Linux hosts where `/sys/class/powercap` is readable, `HardwareProbe` automatically switches to hardware RAPL Joules.

---

## 5. Measured Results by Category (3-Run Median)

### 5.1 `video-stream` (4.40 MiB, 2 files, mean entropy 8.000 bpb)

_ISO-BMFF MP4 container headers (`ftyp`/`moov`/`trak`) + H.264 NAL framing + high-entropy compressed media frames (~7.998 bpb)_

| Tool / Profile | Orig (MiB) | Comp (MiB) | Ratio | Savings | Comp (MiB/s) | Decomp (MiB/s) | CPU Total (ms) | CPU Util % | Avg RAM (MiB) | Peak RAM (MiB) | CPU-s / GiB Saved | Est. Energy (mWh) | SHA-256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|
| **UltraCompress (Instant)** | 4.40 | 4.40 | 1.000x | -0.01% | 337.8 | 309.1 | 9.7 | 74.8% | 61.9 | 64.9 | N/A | 0.0406 | PASS |
| **UltraCompress (Balanced)** | 4.40 | 4.40 | 1.000x | -0.01% | 319.2 | 313.1 | 10.5 | 76.0% | 61.9 | 64.9 | N/A | 0.0437 | PASS |
| **UltraCompress (Archive)** | 4.40 | 4.40 | 1.000x | -0.01% | 367.6 | 334.0 | 14.3 | 119.1% | 61.9 | 64.9 | N/A | 0.0594 | PASS |
| **UltraCompress (Tiny)** | 4.40 | 4.40 | 1.000x | -0.01% | 299.1 | 258.4 | 13.6 | 92.2% | 63.2 | 67.4 | N/A | 0.0565 | PASS |
| **7-Zip (LZMA2 -mx=5)** | 4.40 | 4.40 | 1.000x | -0.01% | 23.9 | 379.6 | 293.4 | 159.3% | 50.7 | 68.0 | N/A | 1.2225 | PASS |
| **7-Zip (LZMA2 -mx=9)** | 4.40 | 4.40 | 1.000x | -0.01% | 23.8 | 394.0 | 292.9 | 158.5% | 51.2 | 68.0 | N/A | 1.2204 | PASS |
| **Zstandard (zstd -3)** | 4.40 | 4.40 | 1.000x | -0.03% | 478.3 | 983.0 | 12.1 | 131.5% | 8.3 | 13.5 | N/A | 0.0504 | PASS |
| **Zstandard (zstd -19)** | 4.40 | 4.40 | 1.000x | -0.01% | 12.3 | 812.0 | 352.1 | 98.2% | 86.8 | 94.8 | N/A | 1.4671 | PASS |
| **Brotli (brotli -q 6)** | 4.40 | 4.40 | 1.000x | -0.05% | 115.0 | 679.3 | 37.4 | 97.8% | 11.3 | 15.4 | N/A | 0.1559 | PASS |
| **Brotli (brotli -q 11)** | 4.40 | 4.40 | 1.000x | -0.05% | 1.9 | 596.5 | 2342.4 | 99.3% | 35.6 | 58.5 | N/A | 9.7600 | PASS |
| **GZip (gzip -6)** | 4.40 | 4.40 | 1.000x | -0.02% | 40.5 | 204.5 | 108.2 | 99.5% | 3.5 | 3.6 | N/A | 0.4510 | PASS |
| **LibDeflate (libdeflate-gzip -6)** | 4.40 | 4.40 | 1.000x | -0.01% | 85.0 | 602.5 | 52.6 | 101.6% | 9.2 | 11.0 | N/A | 0.2194 | PASS |
| **LibDeflate (libdeflate-gzip -12)** | 4.40 | 4.40 | 1.000x | -0.01% | 44.8 | 587.5 | 103.2 | 105.0% | 11.1 | 15.2 | N/A | 0.4299 | PASS |

### 5.2 `source-repo` (1.21 MiB, 112 files, mean entropy 4.951 bpb)

_Multi-file C#, TypeScript, JSON, and Markdown repository tree with shared domain identifiers and exact duplicate vendored SDK files_

| Tool / Profile | Orig (MiB) | Comp (MiB) | Ratio | Savings | Comp (MiB/s) | Decomp (MiB/s) | CPU Total (ms) | CPU Util % | Avg RAM (MiB) | Peak RAM (MiB) | CPU-s / GiB Saved | Est. Energy (mWh) | SHA-256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|
| **UltraCompress (Instant)** | 1.21 | 0.06 | 20.656x | 95.16% | 248.4 | 139.8 | 6.8 | 139.7% | 61.2 | 66.3 | 6.05 | 0.0284 | PASS |
| **UltraCompress (Balanced)** | 1.21 | 0.05 | 23.405x | 95.73% | 126.4 | 138.4 | 12.2 | 127.5% | 61.2 | 66.3 | 10.80 | 0.0509 | PASS |
| **UltraCompress (Archive)** | 1.21 | 0.05 | 25.303x | 96.05% | 43.3 | 117.3 | 30.8 | 110.1% | 68.7 | 87.0 | 27.08 | 0.1281 | PASS |
| **UltraCompress (Tiny)** | 1.21 | 0.04 | 27.901x | 96.42% | 0.6 | 118.6 | 3804.6 | 172.7% | 97.4 | 152.6 | 3337.59 | 15.8526 | PASS |
| **7-Zip (LZMA2 -mx=5)** | 1.21 | 0.04 | 29.246x | 96.58% | 18.1 | 142.4 | 81.6 | 122.0% | 20.1 | 25.8 | 71.46 | 0.3400 | PASS |
| **7-Zip (LZMA2 -mx=9)** | 1.21 | 0.04 | 31.412x | 96.82% | 11.4 | 151.4 | 127.3 | 119.8% | 20.5 | 27.3 | 111.24 | 0.5306 | PASS |
| **Zstandard (zstd -3)** | 1.21 | 0.05 | 24.078x | 95.85% | 262.6 | 427.1 | 4.4 | 94.7% | 4.8 | 5.3 | 3.85 | 0.0182 | PASS |
| **Zstandard (zstd -19)** | 1.21 | 0.04 | 30.862x | 96.76% | 3.0 | 399.8 | 405.3 | 100.1% | 37.5 | 38.8 | 354.32 | 1.6889 | PASS |
| **Brotli (brotli -q 6)** | 1.21 | 0.04 | 29.600x | 96.62% | 110.0 | 383.6 | 11.3 | 102.5% | 5.5 | 9.4 | 9.88 | 0.0470 | PASS |
| **Brotli (brotli -q 11)** | 1.21 | 0.04 | 34.146x | 97.07% | 0.7 | 328.1 | 1765.8 | 100.2% | 23.6 | 29.8 | 1538.59 | 7.3575 | PASS |
| **GZip (gzip -6)** | 1.21 | 0.06 | 20.425x | 95.10% | 134.0 | 228.7 | 8.7 | 96.3% | 2.5 | 3.4 | 7.74 | 0.0362 | PASS |
| **LibDeflate (libdeflate-gzip -6)** | 1.21 | 0.06 | 21.441x | 95.34% | 262.6 | 549.3 | 4.4 | 95.6% | 2.6 | 2.9 | 3.91 | 0.0184 | PASS |
| **LibDeflate (libdeflate-gzip -12)** | 1.21 | 0.05 | 24.032x | 95.84% | 2.6 | 425.5 | 468.7 | 99.9% | 9.7 | 11.2 | 413.66 | 1.9530 | PASS |

### 5.3 `database-dump` (4.40 MiB, 4 files, mean entropy 4.750 bpb)

_Structured SQL dump + telemetry CSV export + fixed 8-byte & 32-byte stride binary pages with numeric correlation_

| Tool / Profile | Orig (MiB) | Comp (MiB) | Ratio | Savings | Comp (MiB/s) | Decomp (MiB/s) | CPU Total (ms) | CPU Util % | Avg RAM (MiB) | Peak RAM (MiB) | CPU-s / GiB Saved | Est. Energy (mWh) | SHA-256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|
| **UltraCompress (Instant)** | 4.40 | 0.83 | 5.315x | 81.18% | 217.7 | 253.6 | 21.0 | 103.9% | 65.4 | 70.1 | 6.02 | 0.0875 | PASS |
| **UltraCompress (Balanced)** | 4.40 | 0.52 | 8.435x | 88.15% | 138.3 | 230.6 | 34.9 | 109.7% | 69.0 | 77.3 | 9.21 | 0.1453 | PASS |
| **UltraCompress (Archive)** | 4.40 | 0.48 | 9.172x | 89.10% | 27.3 | 160.4 | 167.0 | 103.6% | 85.3 | 96.4 | 43.64 | 0.6958 | PASS |
| **UltraCompress (Tiny)** | 4.40 | 0.43 | 10.295x | 90.29% | 3.4 | 162.0 | 2827.0 | 218.3% | 109.5 | 162.1 | 729.05 | 11.7793 | PASS |
| **7-Zip (LZMA2 -mx=5)** | 4.40 | 0.57 | 7.770x | 87.13% | 9.3 | 102.5 | 825.0 | 175.0% | 49.0 | 68.0 | 220.48 | 3.4377 | PASS |
| **7-Zip (LZMA2 -mx=9)** | 4.40 | 0.56 | 7.890x | 87.33% | 7.1 | 99.2 | 1101.7 | 178.7% | 49.3 | 68.0 | 293.75 | 4.5905 | PASS |
| **Zstandard (zstd -3)** | 4.40 | 0.94 | 4.659x | 78.54% | 215.8 | 623.3 | 21.3 | 104.6% | 8.2 | 11.0 | 6.32 | 0.0888 | PASS |
| **Zstandard (zstd -19)** | 4.40 | 0.64 | 6.819x | 85.33% | 3.1 | 511.3 | 1422.7 | 100.0% | 88.9 | 90.9 | 388.19 | 5.9280 | PASS |
| **Brotli (brotli -q 6)** | 4.40 | 0.73 | 5.989x | 83.30% | 30.3 | 341.2 | 144.9 | 99.8% | 14.6 | 22.2 | 40.51 | 0.6039 | PASS |
| **Brotli (brotli -q 11)** | 4.40 | 0.61 | 7.185x | 86.08% | 0.8 | 348.9 | 5224.6 | 99.8% | 48.5 | 69.8 | 1413.15 | 21.7692 | PASS |
| **GZip (gzip -6)** | 4.40 | 0.93 | 4.737x | 78.89% | 28.0 | 201.1 | 156.6 | 99.7% | 3.5 | 3.7 | 46.23 | 0.6526 | PASS |
| **LibDeflate (libdeflate-gzip -6)** | 4.40 | 0.90 | 4.863x | 79.44% | 99.0 | 568.8 | 40.8 | 91.8% | 5.7 | 8.4 | 11.95 | 0.1698 | PASS |
| **LibDeflate (libdeflate-gzip -12)** | 4.40 | 0.83 | 5.311x | 81.17% | 5.8 | 495.5 | 759.1 | 100.0% | 12.9 | 15.9 | 217.75 | 3.1630 | PASS |

### 5.4 `photo-collection` (7.70 MiB, 13 files, mean entropy 7.546 bpb)

_Heterogeneous camera import mixing high-entropy JPEG scans (~7.98 bpb), uncompressed 24-bit BMP & 32-bit RGBA TIFF rasters, and duplicate burst shots_

| Tool / Profile | Orig (MiB) | Comp (MiB) | Ratio | Savings | Comp (MiB/s) | Decomp (MiB/s) | CPU Total (ms) | CPU Util % | Avg RAM (MiB) | Peak RAM (MiB) | CPU-s / GiB Saved | Est. Energy (mWh) | SHA-256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|
| **UltraCompress (Instant)** | 7.70 | 4.96 | 1.552x | 35.59% | 232.1 | 303.8 | 31.6 | 95.4% | 69.9 | 74.6 | 11.82 | 0.1318 | PASS |
| **UltraCompress (Balanced)** | 7.70 | 3.37 | 2.282x | 56.18% | 107.2 | 240.9 | 72.2 | 100.6% | 72.3 | 78.6 | 17.09 | 0.3009 | PASS |
| **UltraCompress (Archive)** | 7.70 | 3.04 | 2.530x | 60.48% | 10.8 | 229.3 | 721.2 | 101.0% | 99.9 | 104.3 | 158.57 | 3.0051 | PASS |
| **UltraCompress (Tiny)** | 7.70 | 2.96 | 2.598x | 61.52% | 1.4 | 72.9 | 10722.7 | 194.4% | 171.9 | 259.6 | 2317.81 | 44.6780 | PASS |
| **7-Zip (LZMA2 -mx=5)** | 7.70 | 3.54 | 2.174x | 54.01% | 12.9 | 76.9 | 850.8 | 142.4% | 67.7 | 97.4 | 209.48 | 3.5450 | PASS |
| **7-Zip (LZMA2 -mx=9)** | 7.70 | 3.54 | 2.174x | 54.01% | 12.4 | 78.9 | 901.5 | 145.0% | 67.5 | 97.5 | 221.95 | 3.7562 | PASS |
| **Zstandard (zstd -3)** | 7.70 | 6.24 | 1.235x | 19.00% | 201.6 | 621.7 | 46.2 | 120.9% | 14.9 | 19.3 | 32.31 | 0.1924 | PASS |
| **Zstandard (zstd -19)** | 7.70 | 4.07 | 1.890x | 47.09% | 10.1 | 382.8 | 743.5 | 97.5% | 92.7 | 98.3 | 209.96 | 3.0980 | PASS |
| **Brotli (brotli -q 6)** | 7.70 | 5.61 | 1.372x | 27.12% | 16.5 | 167.5 | 466.6 | 100.0% | 19.5 | 28.9 | 228.78 | 1.9443 | PASS |
| **Brotli (brotli -q 11)** | 7.70 | 3.65 | 2.109x | 52.59% | 0.5 | 148.3 | 15955.2 | 100.0% | 87.8 | 118.3 | 4034.16 | 66.4801 | PASS |
| **GZip (gzip -6)** | 7.70 | 5.60 | 1.374x | 27.22% | 34.1 | 135.5 | 225.4 | 99.9% | 3.6 | 3.6 | 110.11 | 0.9392 | PASS |
| **LibDeflate (libdeflate-gzip -6)** | 7.70 | 5.51 | 1.397x | 28.45% | 80.7 | 303.7 | 94.4 | 99.0% | 9.8 | 16.2 | 44.12 | 0.3933 | PASS |
| **LibDeflate (libdeflate-gzip -12)** | 7.70 | 5.41 | 1.423x | 29.70% | 18.4 | 278.0 | 417.4 | 99.8% | 13.6 | 20.3 | 186.86 | 1.7391 | PASS |

### 5.5 `native-binaries` (4.40 MiB, 4 files, mean entropy 5.753 bpb)

_PE (`.dll`/`.exe`) and ELF (`.so`) x86-64 binaries with `CALL`/`JMP rel32` code sections targeting shared subroutines plus UTF-8/UTF-16 resource tables_

| Tool / Profile | Orig (MiB) | Comp (MiB) | Ratio | Savings | Comp (MiB/s) | Decomp (MiB/s) | CPU Total (ms) | CPU Util % | Avg RAM (MiB) | Peak RAM (MiB) | CPU-s / GiB Saved | Est. Energy (mWh) | SHA-256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|
| **UltraCompress (Instant)** | 4.40 | 1.48 | 2.979x | 66.43% | 207.5 | 222.6 | 23.1 | 108.8% | 65.8 | 70.8 | 8.08 | 0.0961 | PASS |
| **UltraCompress (Balanced)** | 4.40 | 0.83 | 5.283x | 81.07% | 88.4 | 225.1 | 47.5 | 95.5% | 69.3 | 73.4 | 13.65 | 0.1981 | PASS |
| **UltraCompress (Archive)** | 4.40 | 0.79 | 5.560x | 82.01% | 8.2 | 198.1 | 527.6 | 98.0% | 126.6 | 133.2 | 149.72 | 2.1984 | PASS |
| **UltraCompress (Tiny)** | 4.40 | 0.69 | 6.415x | 84.41% | 1.4 | 73.0 | 5871.4 | 182.6% | 188.4 | 239.2 | 1618.80 | 24.4643 | PASS |
| **7-Zip (LZMA2 -mx=5)** | 4.40 | 1.06 | 4.166x | 76.00% | 8.3 | 78.6 | 882.7 | 165.8% | 32.1 | 50.2 | 270.31 | 3.6778 | PASS |
| **7-Zip (LZMA2 -mx=9)** | 4.40 | 1.03 | 4.280x | 76.64% | 8.0 | 82.3 | 895.1 | 163.2% | 34.3 | 49.9 | 271.82 | 3.7295 | PASS |
| **Zstandard (zstd -3)** | 4.40 | 1.37 | 3.216x | 68.91% | 215.2 | 639.4 | 21.5 | 105.0% | 8.9 | 11.5 | 7.25 | 0.0895 | PASS |
| **Zstandard (zstd -19)** | 4.40 | 1.27 | 3.457x | 71.07% | 2.8 | 536.1 | 1596.8 | 100.6% | 89.6 | 91.6 | 522.88 | 6.6533 | PASS |
| **Brotli (brotli -q 6)** | 4.40 | 1.33 | 3.303x | 69.72% | 26.8 | 311.1 | 165.1 | 100.4% | 14.2 | 21.5 | 55.11 | 0.6879 | PASS |
| **Brotli (brotli -q 11)** | 4.40 | 1.18 | 3.725x | 73.15% | 0.7 | 250.8 | 6287.0 | 99.9% | 49.7 | 69.3 | 2000.16 | 26.1956 | PASS |
| **GZip (gzip -6)** | 4.40 | 1.37 | 3.221x | 68.95% | 18.9 | 183.2 | 232.4 | 99.7% | 3.5 | 3.6 | 78.44 | 0.9684 | PASS |
| **LibDeflate (libdeflate-gzip -6)** | 4.40 | 1.39 | 3.170x | 68.46% | 109.8 | 467.4 | 39.7 | 99.2% | 5.9 | 8.7 | 13.51 | 0.1656 | PASS |
| **LibDeflate (libdeflate-gzip -12)** | 4.40 | 1.29 | 3.410x | 70.67% | 5.3 | 472.4 | 825.7 | 99.9% | 12.7 | 15.4 | 271.92 | 3.4406 | PASS |

### 5.6 `mixed-workspace` (7.74 MiB, 66 files, mean entropy 6.544 bpb)

_Combined heterogeneous workspace containing video segments, source repo + duplicate vendor tree, database dump, photo collection, and native x86-64 binaries_

| Tool / Profile | Orig (MiB) | Comp (MiB) | Ratio | Savings | Comp (MiB/s) | Decomp (MiB/s) | CPU Total (ms) | CPU Util % | Avg RAM (MiB) | Peak RAM (MiB) | CPU-s / GiB Saved | Est. Energy (mWh) | SHA-256 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|
| **UltraCompress (Instant)** | 7.74 | 4.21 | 1.838x | 45.60% | 233.6 | 250.8 | 32.0 | 96.7% | 70.4 | 79.7 | 9.29 | 0.1334 | PASS |
| **UltraCompress (Balanced)** | 7.74 | 3.22 | 2.402x | 58.37% | 125.3 | 250.2 | 63.8 | 103.4% | 72.7 | 80.8 | 14.47 | 0.2659 | PASS |
| **UltraCompress (Archive)** | 7.74 | 3.19 | 2.425x | 58.76% | 21.8 | 232.0 | 363.4 | 102.3% | 84.2 | 105.4 | 81.85 | 1.5141 | PASS |
| **UltraCompress (Tiny)** | 7.74 | 3.07 | 2.520x | 60.32% | 2.1 | 104.2 | 6930.4 | 189.9% | 147.6 | 231.9 | 1520.60 | 28.8767 | PASS |
| **7-Zip (LZMA2 -mx=5)** | 7.74 | 3.44 | 2.249x | 55.54% | 14.8 | 90.1 | 884.7 | 169.6% | 55.3 | 94.5 | 210.84 | 3.6864 | PASS |
| **7-Zip (LZMA2 -mx=9)** | 7.74 | 3.43 | 2.258x | 55.72% | 13.4 | 92.9 | 979.1 | 170.2% | 56.4 | 94.6 | 232.59 | 4.0797 | PASS |
| **Zstandard (zstd -3)** | 7.74 | 4.43 | 1.745x | 42.70% | 273.4 | 935.5 | 31.8 | 112.4% | 13.1 | 17.9 | 9.86 | 0.1326 | PASS |
| **Zstandard (zstd -19)** | 7.74 | 3.78 | 2.044x | 51.09% | 5.3 | 527.1 | 1448.7 | 99.3% | 92.6 | 96.6 | 375.34 | 6.0364 | PASS |
| **Brotli (brotli -q 6)** | 7.74 | 3.85 | 2.007x | 50.18% | 26.4 | 269.3 | 295.9 | 101.0% | 18.1 | 24.2 | 78.05 | 1.2331 | PASS |
| **Brotli (brotli -q 11)** | 7.74 | 3.53 | 2.192x | 54.39% | 0.5 | 176.8 | 14766.6 | 100.0% | 87.8 | 123.1 | 3593.33 | 61.5273 | PASS |
| **GZip (gzip -6)** | 7.74 | 4.49 | 1.723x | 41.97% | 33.2 | 167.7 | 232.1 | 99.6% | 3.6 | 3.6 | 73.20 | 0.9672 | PASS |
| **LibDeflate (libdeflate-gzip -6)** | 7.74 | 4.46 | 1.734x | 42.32% | 98.9 | 470.6 | 77.5 | 99.0% | 9.1 | 14.9 | 24.23 | 0.3228 | PASS |
| **LibDeflate (libdeflate-gzip -12)** | 7.74 | 4.38 | 1.768x | 43.44% | 8.8 | 421.4 | 883.3 | 99.9% | 15.0 | 22.8 | 269.13 | 3.6803 | PASS |

---

## 6. Engineering Analysis: Where UltraCompress Wins vs. Where Single Codecs Win

### Where UltraCompress Wins Decisively

1. **Mixed Real-World Workspaces (`mixed-workspace`) — Simultaneous Ratio, Speed & Energy Leadership**:
   - **`UltraCompress (Balanced)`** achieves **`2.402x` (`58.37%` savings) at `125.3 MiB/s` and `14.47 CPU-s / GiB saved`** (`0.2659 mWh`). It produces a **smaller** archive than **`7-Zip -mx=9`** (`2.258x` at `13.4 MiB/s`, `232.59 CPU-s/GiB`), **`Brotli -q 11`** (`2.192x` at `0.5 MiB/s`, `3593.33 CPU-s/GiB`), and **`Zstd -19`** (`2.044x` at `5.3 MiB/s`, `375.34 CPU-s/GiB`) while running **`9.4x` to `250x` faster** and using **`16x` to `248x` less CPU energy per GiB saved**.
   - **`UltraCompress (Tiny)`** reaches **`2.520x` (`60.32%` savings)**—the highest compression ratio across all 13 tested configurations—by pairing SHA-256 deduplication and media `Store` bypass with `X86Bcj -> LZMA2-9` on native binaries, `ByteShuffle3/4Delta -> LZMA2-9` on rasters, `ByteShuffle8Delta -> Zstd-22` on columnar numeric series, and `Brotli-11` on source/32-byte pages.
   - **`UltraCompress (Instant)`** (`1.838x`, `9.29 CPU-s / GiB saved`) beats `zstd -3` (`1.745x`, `9.86 CPU-s / GiB saved`) on both ratio and CPU efficiency per GiB saved because skipping `MP4`/`JPEG` blocks and deduplicating exact file copies saves more bytes than running `zstd -3` across incompressible media.

2. **Structured Numeric & Database Dumps (`database-dump`) — Breaking the Raw LZ77 Ceiling**:
   - Byte-oriented LZ77 codecs lose match continuity when 8-byte timestamps, prices, and row counters vary in their low-order bytes every 8 or 32 bytes (`zstd -19` stalls at `6.819x`, `brotli -q 11` at `7.185x`, `7za -mx=9` at `7.890x`).
   - UltraCompress's O(1) sample probe detects 8-byte and 32-byte stride correlation and applies reversible `ByteShuffle + Delta` before entropy coding: **`Balanced` reaches `8.435x` at `138.3 MiB/s` (`9.21 CPU-s/GiB`)**—beating `7za -mx=9` in ratio while running **`19.5x` faster** and using **`31.9x` less CPU per GiB saved**—and **`Tiny` reaches `10.295x` (`90.29%` savings)**.

3. **Heterogeneous Photo & Media Imports (`photo-collection` & `video-stream`)**:
   - On `photo-collection`, **`UltraCompress (Balanced)` (`2.282x` at `107.2 MiB/s`)** and **`Tiny` (`2.598x`, `61.52%` saved)** outperform every competitor (`7za -mx=9`: `2.174x` at `12.4 MiB/s`; `zstd -19`: `1.890x`; `zstd -3`: `1.235x`) because JPEGs bypass encoding (`store=6`), burst copies deduplicate in O(1) (`dedup=3`), and uncompressed 24-bit `BMP` / 32-bit `TIFF` rasters undergo 3- and 4-byte channel-shuffle delta coding.
   - On `video-stream` (`8.000 bpb` MP4), `brotli -q 11` wastes `2,342 ms` CPU (`9.76 mWh`) and `zstd -19` wastes `352 ms` CPU (`1.47 mWh`) to save `0 bytes`, whereas UltraCompress bypasses immediately (`9.7–14.3 ms` CPU, `0.041–0.059 mWh`—up to **`240x` less wasted energy**).

4. **x86-64 Native Executables (`native-binaries`)**:
   - Converting relative `E8`/`E9` `CALL`/`JMP rel32` displacements into absolute target offsets via `X86Bcj` lets **`UltraCompress (Balanced)` reach `5.283x` at `88.4 MiB/s` (`13.65 CPU-s/GiB`)** and **`Tiny` reach `6.415x` (`84.41%` savings)**, substantially ahead of `zstd -19` (`3.457x`), `brotli -q 11` (`3.725x`), and `7za -mx=9` (`4.280x` when Linux ELF + Windows PE binaries are archived together).

### Where Single-Codec Competitors Win (Honest Trade-Offs)

1. **Small, Homogeneous Text Trees (`source-repo`, `1.21 MiB` / `112` files)**:
   - On pure text/code without media or binary strides, solid `.tar` + **`brotli -q 11` (`34.146x`)**, **`7za -mx=9` (`31.412x`)**, **`zstd -19` (`30.862x`)**, and **`brotli -q 6` (`29.600x` at `110.0 MiB/s`)** achieve higher archive ratios than `UltraCompress (Tiny)` (`27.901x`) and `Archive` (`25.303x`).
   - **Root cause**: Every `.ucx` archive embeds an uncompressed O(1) random-access manifest storing each file's relative path, byte offset, length, and 32-byte `SHA-256` digest (`~7.5 KiB` across 112 files) to support instant browsing and selective single-file extraction (`uc extract --select`) without scanning a sequential `.tar` stream. When `1.21 MiB` of repetitive code compresses down to `~38 KiB`, `7.5 KiB` of per-entry SHA-256 metadata represents `~16.5%` of the final `.ucx` file (`45.5 KiB` vs `37.2 KiB` for `tar.br`). On larger repositories (`100+ MiB`), this ~68-byte-per-file index amortizes below `0.2%`.

2. **Minimal Memory Footprint (`< 15 MiB` Peak RSS)**:
   - Standalone C binaries (`gzip -6` at **`3.4–3.7 MiB`**, `libdeflate-gzip -6` at **`2.9–16.2 MiB`**, and `zstd -3` at **`5.3–19.3 MiB`**) run without a managed `.NET 8` runtime heap (`64.9–80.8 MiB` Peak RSS for `UltraCompress Instant`/`Balanced`). In severely memory-constrained embedded/initramfs environments (`< 32 MiB` RAM), native `zstd` or `libdeflate` is the right tool.

3. **Pure Single-Stream Decompression Speed & Raw Bypass Overhead**:
   - Sequential C stream decoders (`zstd -d` at **`427–983 MiB/s`** and `libdeflate-gzip -d` at **`304–603 MiB/s`** writing a single contiguous `.tar` file) achieve higher raw MB/s than `.ucx` directory extraction (`138–334 MiB/s`), which hashes every restored block AND every individual extracted file with `SHA-256` before committing it to disk. Likewise, on pure `video-stream`, `zstd -3`'s native C literal-skip loop (`478.3 MiB/s`) edges out `.ucx`'s SHA-256-verified copy (`337.8–367.6 MiB/s`).
