# UltraCompress — user guide (Phase 1)

UltraCompress shrinks files using **pipelines**: optional preprocessors, then codecs (zstd first; 7z and others later). You pick a **profile** that controls how hard the tool tries.

## Profiles

| Profile | Behavior |
|---------|----------|
| **Balanced** | Single default recipe, zstd moderate level; target under ~30s per typical file. |
| **Small** | Stronger zstd level + light preprocess placeholder; for everyday “make it smaller” use. |
| **Tiny** | High compression level; future: multi-candidate tournament (~10 min budget). |
| **Laboratory** | Maximum effort; optional lossy transforms when enabled; manual recipe edits. |
| **Experimental** | Plugins, PAQ-family backends (download on first use), unstable switches — crash-isolated in worker processes. |

## CLI quick start

```text
dotnet run --project src/UltraCompress.Worker -- compress --input "C:\Files\report.pdf" --profile Small
```

- **`--output`**: optional path; default is the input path plus `.uc.zst`.
- Verification: by default the worker decompresses in memory and checks the result matches the original (SHA-256 optional).

## Tray and service

Phase 2 adds the system tray (WinUI 3) and the `UltraCompressEngine` Windows Service for background queues. This repo currently ships the **worker CLI** and architectural stubs only.

## Explorer integration (stub)

Phase 4 adds **UltraCompress here…** in File Explorer. The managed stub writes queue requests to `%ProgramData%\UltraCompress\queue\inbox.jsonl`. Registering the context menu on your PC requires signing the sparse package — see [OPERATOR_SIGNING.md](./OPERATOR_SIGNING.md) and [SHELL_EXTENSION.md](./SHELL_EXTENSION.md).

## Safety

Experimental tier should only be used after accepting instability. Workers run out-of-process so codec failures do not tear down the tray or engine.