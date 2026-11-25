# Lossless Profile SLAs & Four-Mode Data Safety Guarantee

UltraCompress enforces two non-negotiable engineering guarantees across its core engine, `.ucx` container, CLI, and WinUI 3 Desktop application:

1. **Every compression profile (`Instant`, `Balanced`, `Archive`, `Tiny`, `Laboratory`) is 100% bit-exact lossless.** Lossy transforms, silent media re-encoding, and metadata stripping are strictly prohibited inside compression profiles.
2. **Every mutating workflow (`LossyOptimization`, `FormatConversion`, `DataRemoval`) is isolated behind an explicit `OperationKind` boundary** requiring informed user consent, an itemized impact or fidelity preview, and `PreserveOriginal = true` by default.

---

## 1. Five Measurable, 100% Bit-Exact Lossless Profiles

Generic archivers expose opaque numeric levels (`-1` through `-9` or `-mx=9`) that give no guarantee of memory usage, throughput floor, or behavior on incompressible data. UltraCompress defines each profile via an explicit `ProfileContract` SLA.

### Profile Contract Matrix

| Profile | Restoration Guarantee | Target Compress Floor¹ | Target Decompress Floor¹ | Peak Worker RAM Cap | High-Entropy Bypass Gate | Routing, Preprocessors & Candidate Search |
|---------|-----------------------|------------------------|--------------------------|---------------------|--------------------------|-------------------------------------------|
| **`Instant`** | **100% Bit-Exact** (XXH64 + SHA-256) | `>= 650 MB/s` | `>= 1,800 MB/s` | `128 MiB` | `>= 7.65 bpb` → `FastStreamBypass` | Deterministic single arm (`LibDeflate` / `Zstd-1`), SHA-256 exact-file dedup, zero tournament overhead |
| **`Balanced`** *(Default)* | **100% Bit-Exact** (XXH64 + SHA-256) | `>= 280 MB/s` | `>= 1,400 MB/s` | `256 MiB` | `>= 7.85 bpb` → `FastStreamBypass` | `ContentInspector` single-winner routing: `Zstd-5`, per-class `Delta` / `ByteShuffle` / `X86Bcj`, `32 MiB` solid blocks |
| **`Archive`** | **100% Bit-Exact** (XXH64 + SHA-256) | `>= 85 MB/s` | `>= 1,100 MB/s` | `768 MiB` | `>= 7.92 bpb` → `FastStreamBypass` | Long-window `Zstd-15` (`64 MiB` window) or `Brotli-9`, extension-sorted `64 MiB` solid dictionary blocks, full transform chain |
| **`Tiny`** | **100% Bit-Exact** (XXH64 + SHA-256) | `>= 22 MB/s` | `>= 600 MB/s` | `1,536 MiB` | `>= 7.95 bpb` → `FastStreamBypass` | **4-arm chunk pilot + UCB1 tournament** (`Zstd-19`, `LZMA2-7`, `Brotli-11`, `ByteShuffle+Zstd`) with `60 s` search cap; caches winner in SQLite |
| **`Laboratory`** | **100% Bit-Exact** (XXH64 + SHA-256) | `>= 6 MB/s` | `>= 220 MB/s` | `4,096 MiB` | `>= 7.98 bpb` → `FastStreamBypass` | **8-arm exhaustive UCB1 bandit tournament** (`LZMA2 -mx=9` ultra dict, `Zstd-22 --ultra`, chained `Delta`/`ByteShuffle`/`X86Bcj`) with `300 s` search cap; **strictly lossless** |

*¹ Throughput floors measured on reference 8C/16T x64 NVMe hardware on mixed compressible workloads; streams routed through `FastStreamBypass` run at disk I/O speed (`2,000+ MB/s`) across all profiles.*

*(CLI compatibility note: legacy `--profile Small` maps to `Archive`, and legacy `--profile Experimental` maps to `Laboratory` under strict lossless verification.)*

### Reversible Lossless Preprocessors

Before entropy/dictionary coding, UltraCompress applies strictly invertible byte transforms when `ContentInspector` measures structural correlation:

- **`FastStreamBypass` (`Store`)**: Triggered when 5-window Shannon entropy exceeds the profile's bypass threshold (`7.65–7.98 bpb`) and container magic confirms compressed media (`MP4`, `MKV`, `JPEG`, `HEIC`, `WebM`, encrypted blobs). Copies raw blocks directly into `.ucx` with XXH64/SHA-256 digests and zero codec CPU waste.
- **`ByteShuffleTransform` (`stride = 2 | 4 | 8`)**: Transposes fixed-width numeric arrays, IEEE-754 floats, database page headers, and telemetry records so identical byte positions (signs, exponents, high-order integer zeros) group into long runs. Bit-exact inverse transpose runs on decode.
- **`DeltaTransform` (`stride = 1..16`)**: Replaces sequential sample or pixel-channel values (`RAW`, `BMP`, `TIFF`, `WAV`/PCM, monotonic timestamps) with bytewise residuals. Bit-exact prefix-sum reconstruction runs on decode.
- **`X86BcjTransform`**: Converts relative `CALL`/`JMP` (`E8`/`E9`) offsets in Windows `PE` (`.exe`, `.dll`) and Linux `ELF` code sections into absolute target addresses so repeated function calls share identical byte sequences across the binary.
- **SHA-256 Content-Addressed Deduplication**: Identical files anywhere in an input tree hash to a single content-addressed payload block in the `.ucx` manifest; every path metadata entry (`mtime`, attributes, relative path) restores identically.

### Verification Invariant

Every compressed block records its uncompressed length and `XXH64` checksum, and every `.ucx` entry and archive trailer records a cryptographic `SHA-256` digest:

1. **During compression**: Worker pipelines verify candidate chunk round-trips during `Tiny` and `Laboratory` tournaments and compute streaming `SHA-256` over original input bytes. If a compressed block is `>= 99.8%` of raw block size, the block automatically falls back to uncompressed `Store` so archives never inflate payloads.
2. **Atomic staging**: Archives write to `<target>.ucx.part` tracked in SQLite (`jobs.db`) and rename atomically only after final trailer and digest verification succeed.
3. **During extraction & `verify`**: Any single-bit mismatch raises an immediate integrity failure (`ExitCode = 2`), halts finalization, and preserves existing destination files untouched.

---

## 2. Strict Four-Mode Data Safety Boundary (`OperationKind`)

Users must never wonder whether selecting a "maximum compression" setting might degrade photo quality, strip camera EXIF metadata, or alter file formats. UltraCompress enforces four mutually exclusive `OperationKind` modes at the API, CLI, Engine IPC, and Desktop UI layers.

### Mode Comparison

| `OperationKind` | Bit-Exact Original Restorable? | Default in Archive Flows? | Consent Gate Required? | Preview Required Before Run? | `PreserveOriginal` Default |
|-----------------|-------------------------------|---------------------------|------------------------|------------------------------|----------------------------|
| **`LosslessCompression`** | **Yes — 100% bit-exact** | **Yes (all 5 profiles)** | No (non-destructive) | Advisor rationale + size estimate | **Yes** (source untouched; writes `.ucx`) |
| **`LossyOptimization`** | No (perceptual re-encoding) | Never | **Mandatory (`ConsentConfirmation`)** | **Side-by-side visual/SSIM + byte delta** | **Yes — locked by default** |
| **`FormatConversion`** | Container changes (values preserved where applicable) | Never | **Mandatory (`ConsentConfirmation`)** | **Target format, extension & compatibility diff** | **Yes — locked by default** |
| **`DataRemoval`** | No (targeted bytes stripped) | Never | **Mandatory (`ConsentConfirmation`)** | **Itemized manifest of stripped fields/files** | **Yes — locked by default** |

### Mode Specifications & Safeguards

#### Mode 1: `LosslessCompression` (Default)
- Used by drag-and-drop archiving, Explorer **Compress with UltraCompress**, scheduled queue jobs, and `uc pack` / `uc compress`.
- **Guarantee**: Extracting the archive produces bytes with the exact same `SHA-256` digest as the input files.
- **Zero hidden mutations**: `JPEG` files are never re-quantized; `MP4` streams are never re-muxed; `EXIF` tags, trailing newlines, BOMs, and PE signatures are preserved verbatim.

#### Mode 2: `LossyOptimization` (Optimization Studio Only)
- Intended for intentional media delivery workflows (e.g., exporting web-ready `WebP`/`AVIF` copies from studio `JPEG`/`PNG` assets or down-bitrating preview audio/video).
- **Engine & UI Safeguards**:
  - Accessible only in the separate **Optimization & Data Hygiene Studio** tab in Desktop or via explicit `uc optimize --mode lossy` CLI subcommand.
  - Displays a live **Before / After visual Split-View** with perceptual quality score (`SSIM` / `PSNR`) and exact byte savings before execution can be armed.
  - Requires explicit `ConsentConfirmation` (`AcknowledgeIrreversibleQualityChange = true` in IPC/API, or `--consent-lossy` on CLI).
  - Writes to a dedicated sibling directory or `.optimized.<ext>` suffix (`PreserveOriginal = true`). In-place replacement is disabled by default and requires unlocking a secondary confirmation gate.

#### Mode 3: `FormatConversion` (Explicit Container Migration)
- Intended for deterministic schema or container transcoding where the file format intentionally changes (e.g., `BMP`/`TIFF` → lossless `PNG`, `CSV` → `Parquet`, legacy `.zip`/`.7z` → indexed `.ucx`).
- **Engine & UI Safeguards**:
  - Accessible only via **Format Converter** in Optimization Studio or `uc convert --to <format>`.
  - Previews exact source-to-target container mapping, extension changes, and downstream tool compatibility notes.
  - Preserves source files untouched (`PreserveOriginal = true` by default).

#### Mode 4: `DataRemoval` (Metadata & Artifact Hygiene)
- Intended for deliberate privacy hygiene or build-artifact cleanup before sharing (e.g., stripping GPS/camera serial `EXIF`/`XMP` metadata from photos, stripping `PDB`/DWARF debug symbols from release binaries, or excluding `node_modules`/`target`/`.git` caches).
- **Engine & UI Safeguards**:
  - Generates an **Itemized Removal Manifest** listing every metadata tag, stream, or path that will be omitted along with exact byte counts.
  - Requires explicit `ConsentConfirmation` (`AcknowledgeDataRemoval = true` or `--consent-remove-data`).
  - Outputs sanitized copies while leaving the original source tree untouched (`PreserveOriginal = true` by default).

---

## 3. CLI & Engine Contract Enforcement

Engine IPC (`NamedPipeEngineClient` and gRPC `EngineService`) validates `OperationKind` on every job submission:

```bash
# 1. Standard 100% bit-exact lossless archival (no destructive flags possible)
uc pack ./project-folder -o ./backups/project.ucx --profile Balanced

# 2. Explain exact ContentInspector signals and StrategyDecisionReport without writing output
uc analyze ./database-dump.sql --profile Tiny --explain

# 3. Verify bit-exact SHA-256 & XXH64 integrity of an existing archive
uc verify ./backups/project.ucx

# 4. Separate DataRemoval workflow: preview itemized manifest first (dry-run default without consent)
uc hygiene ./holiday-photos --strip exif-gps,camera-serial --preview
uc hygiene ./holiday-photos --strip exif-gps,camera-serial --out ./holiday-photos-clean --consent-remove-data
```

Any attempt to invoke `LossyOptimization`, `FormatConversion`, or `DataRemoval` without a valid `ConsentConfirmation` payload or with `PreserveOriginal = false` lacking explicit override is rejected immediately by `UltraCompress.Core` validation before any worker process starts.
