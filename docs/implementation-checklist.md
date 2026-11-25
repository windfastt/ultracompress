# Implementation & Verification Checklist

Tracks architectural capabilities across `main` and the `v1.0.0` release train.

## Core Engine & Codec Backends

- [x] `UltraCompress.Core` — `FileFingerprint`, `PipelineRecipe`, JSON Schema validation
- [x] Multi-window O(1) `ContentInspector` + explainable `StrategyDecisionReport`
- [x] Five 100% bit-exact lossless `ProfileContract` SLAs (`Instant`, `Balanced`, `Archive`, `Tiny`, `Laboratory`)
- [x] Strict four-mode `OperationKind` boundary (`LosslessCompression`, `LossyOptimization`, `FormatConversion`, `DataRemoval`)
- [x] Reversible lossless preprocessors (`FastStreamBypass`, `DeltaTransform`, `ByteShuffleTransform`, `X86BcjTransform`)
- [x] Verified codec backends: `ZstdSharp` (`zstd`), isolated `7za` (`LZMA2`), `.NET Brotli`, and `LibDeflate`
- [x] `.ucx` multi-file indexed archive container with SHA-256 deduplication and selective extraction
- [x] SQLite `recipes.db` store + UCB1 multi-candidate chunk tournament coordinator

## Windows Service, Desktop Manager & Fleet

- [x] `UltraCompress.Engine` Windows Service + named-pipe IPC (`\\.\pipe\UltraCompress.Engine`) + localhost gRPC (`17832`)
- [x] Distributed LAN worker gRPC fleet (`WorkerService` on `17833` with token auth and lease reaper)
- [x] SQLite `jobs.db` persistent queue journal, atomic `.part` staging, and automatic crash recovery
- [x] WinUI 3 Desktop Archive Manager + Tray (`UltraCompress.Tray`): drag-and-drop, `.ucx` browser, selective extract, history, destination manager, and consent-gated Optimization Studio
- [x] Explorer context-menu enqueue integration (`UltraCompress.Shell` + sparse MSIX manifest)
- [x] WiX installer package & bundle (`UltraCompress.msi` + `UltraCompress-Setup.exe`)

## CI, Benchmarks & Documentation

- [x] GitHub Actions `windows-latest` build, unit test, and golden corpus backend round-trip verification
- [x] Reproducible 6-category competitor benchmark suite (`7-Zip`, `Zstandard`, `Brotli`, `GZip`/`LibDeflate`)
- [x] Profiles & Four-Mode Data Safety specification (`docs/profiles-and-safety.md`)
- [x] Interactive SaaS/Desktop product site & Benchmark Explorer (`docs/site/` → GitHub Pages)
- [ ] Operator EV Authenticode signing secret wired for production release tag (`docs/OPERATOR_SIGNING.md`)
