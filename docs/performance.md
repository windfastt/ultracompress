# Performance & Competitor Benchmarks

UltraCompress tracks both internal engine microbenchmarks (BenchmarkDotNet) and apples-to-apples multi-category competitor benchmarks against `7-Zip` (`7za`), `Zstandard` (`zstd`), `Brotli`, and `GZip`/`LibDeflate`.

## Run Microbenchmarks & CI Smoke Suite

```powershell
# Full BenchmarkDotNet release run
dotnet run -c Release --project tests/UltraCompress.Benchmarks

# Fast deterministic CI verification
dotnet run -c Release --project tests/UltraCompress.Benchmarks -- --ci
```

## Competitor Benchmark Categories & Metrics

Interactive charts, CLI flags, hardware manifests, and downloadable JSON artifacts are published at **[windfastt.github.io/ultracompress/benchmarks.html](https://windfastt.github.io/ultracompress/benchmarks.html)** (`docs/site/data/benchmarks.json`).

Evaluated across 6 standardized 10 GB workload archetypes:
1. **Mixed Developer & Media Workspace** (aggregate real-world directory)
2. **4K H.265 Video (`MP4`/`MKV`)** — verifies `FastStreamBypass` near-disk throughput and zero wasted CPU
3. **Polyglot Source Repository (`185k` files)** — verifies SHA-256 deduplication + extension-sorted solid blocks + `< 45 ms` selective extraction
4. **Database & Telemetry Dump (`SQL`/`SQLite`/`CSV`)** — verifies 4/8-byte `ByteShuffle` + `Delta` preprocessing gains
5. **Mixed Photo Collection (`JPEG`/`HEIC` + `RAW`/`TIFF`/`BMP` + duplicates)** — verifies heterogeneous per-file `Store` vs `Delta+Zstd` splitting with zero lossy mutation
6. **Windows SDK & Native Binaries (`PE` `.exe`/`.dll`)** — verifies `X86Bcj` jump-filter effectiveness

Every run records:
- **Output Size & Lossless Savings (`GiB` / `%` / ratio)**
- **Compression & Decompression Throughput (`MB/s`)**
- **CPU Utilization (`%`) & Total CPU Seconds**
- **Peak Working Set RAM (`MiB`)**
- **Energy Efficiency (`CPU-seconds per GiB saved`)**
- **Bit-Exact Verification (`SHA-256` match)**
