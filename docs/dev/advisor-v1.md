# ContentInspector & Pipeline Advisor Architecture

UltraCompress selects pipelines per stream using deterministic, O(1)-memory content inspection rather than blind file-extension guesses or uniform single-codec settings.

## 1. Five-Window `ContentInspector`

`ContentInspector` samples five fixed-size windows across each stream (`head`, `25%`, `50%`, `75%`, `tail`, bounded at `<= 1 MiB` total working memory regardless of file size) to compute an enriched `FileFingerprint`:

- **Magic & Container Identification (30+ signatures):** Identifies compressed video/audio (`MP4`/`ftyp`, `MKV`/`WebM`, `MP3`, `FLAC`, `OGG`), images (`JPEG`, `PNG`, `HEIC`, `WebP`, `BMP`, `TIFF`, camera `RAW`), databases & structured formats (`SQLite` page headers, `Parquet`, `JSON`/`NDJSON`, `CSV`/`TSV`, `SQL` dumps, `XML`), executables (`PE`/COFF `.exe`/`.dll`, `ELF`), and existing archives (`.zip`, `.7z`, `.zst`, `.gz`, `.ucx`).
- **Multi-Window Shannon Entropy (`0.00–8.00 bpb`):** Distinguishes genuinely incompressible streams (`>= 7.90 bpb` across all five quartiles) from containers with compressed headers followed by compressible payloads.
- **Byte-Stride Autocorrelation (`stride = 2, 3, 4, 8, 16`):** Detects fixed-width numeric columns, IEEE-754 floating-point arrays, monotonic 64-bit keys/timestamps, and uncompressed RGB/RGBA pixel channels.
- **Bigram Predictability & UTF-8 Token Density:** Measures cross-file dictionary affinity for solid source-code and configuration block grouping.
- **SHA-256 Content Addressing:** Identifies exact file duplicates across multi-folder inputs before compression begins.

## 2. Routing Rules & Reversible Preprocessors

| Rule ID | Signal Condition | Selected Transform + Codec Strategy |
|---------|------------------|-------------------------------------|
| `high_entropy_bypass` | All sampled windows exceed profile bypass gate (`7.65–7.98 bpb`) + compressed media/archive magic | `FastStreamBypass` (`Store`) + XXH64/SHA-256 integrity seal (zero codec CPU) |
| `exact_duplicate_dedup` | Matching SHA-256 content hash in `.ucx` manifest | Zero-byte manifest reference to existing verified payload block |
| `numeric_stride_shuffle` | 4-byte or 8-byte stride correlation `r >= 0.45` (`SQLite`, binary dumps, telemetry, floats) | `ByteShuffleTransform(stride)` + `DeltaTransform` → long-window `Zstd` / `LZMA2` |
| `raster_channel_delta` | Uncompressed raster (`BMP`, `TIFF`, uncompressed `RAW`, `PCM`) with 2/3/4-byte channel correlation | `DeltaTransform(channels)` → `Zstd` / `LZMA2` |
| `native_executable_bcj` | `PE` (`.exe`/`.dll`) or `ELF` machine code section | `X86BcjTransform` (`CALL`/`JMP` offset normalization) → `Zstd` / `LZMA2` |
| `solid_source_group` | High token repetition (`< 5.2 bpb`) across many small text/source files | Extension-sorted `32–64 MiB` solid block + trained/long-window `Zstd` |
| `tournament_ucb1` | `Tiny` (4 arms) or `Laboratory` (8 arms) on compressible non-trivial streams | Chunk pilot + UCB1 multi-armed bandit across `Zstd`, `7za LZMA2`, `Brotli`, `LibDeflate` + transforms; winner cached in SQLite `recipes.db` |

## 3. Explainable `StrategyDecisionReport`

Every routing call emits a structured `StrategyDecisionReport` available in Desktop rationale cards and via `analyze --explain` (text or `--json`):

- **Observed Signals:** Container/magic match, 5-window entropy vector, peak stride correlation, duplicate file count.
- **Selected Pipeline:** Exact transform chain, codec, level, window size, and block layout.
- **Rejected Alternatives:** Explicit reasons competitor codecs or heavier levels were skipped (e.g., *"Skipped LZMA2-9: 5-window entropy 7.984 bpb >= 7.85 bpb bypass gate; expected gain < 0.1% at > 300 CPU-sec/GiB"*).
- **Safety Verification:** Confirms `OperationKind.LosslessCompression` and `XXH64` + `SHA-256` verification status.
