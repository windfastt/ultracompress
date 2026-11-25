# Testing & Verification

Run the complete `.NET 8` test suite in Release configuration:

```powershell
dotnet build src/UltraCompress.sln -c Release
dotnet test src/UltraCompress.sln -c Release --no-build
```

## Test Coverage Layers

- **`UltraCompress.Core.Tests`:** `PipelineRecipe` JSON schema validation, `ProfileContract` lossless SLAs, `OperationKind` consent-gate invariants, `ContentInspector` entropy/stride/magic classification, reversible preprocessors (`Delta`, `ByteShuffle`, `X86Bcj`), SQLite `recipes.db` persistence, and UCB1 tournament selection.
- **`UltraCompress.Worker.Tests`:** End-to-end golden corpus round-trip verification across `ZstdSharp`, `7za` (`LZMA2`), `Brotli`, and `LibDeflate` backends (`FullyQualifiedName~GoldenFixture`), `.ucx` multi-file deduplication, and `SHA-256` / `XXH64` corruption detection.
- **`UltraCompress.Engine.Tests`:** Named-pipe IPC (`\\.\pipe\UltraCompress.Engine`), gRPC `EngineService` and distributed `WorkerService` lease recovery, destination pre-flight checks, and SQLite `jobs.db` crash-recovery journaling.
