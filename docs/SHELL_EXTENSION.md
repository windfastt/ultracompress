# Explorer shell extension (Phase 4 stub)

UltraCompress adds **UltraCompress here…** to the Windows 11 File Explorer context menu for files and folders. Phase 4 in the roadmap delivers the full native handler; this repo currently ships the **managed enqueue stub** and **sparse package manifest template**.

## Architecture

```text
Explorer  →  IExplorerCommand (native DLL, TODO)
         →  ShellEnqueueClient  →  %ProgramData%\UltraCompress\queue\inbox.jsonl
         →  UltraCompress.Engine (future: tail inbox and schedule jobs)
```

The native DLL must only enqueue paths and a profile. Compression runs in worker processes under the engine service.

## Sparse package

Files live under `src/UltraCompress.Shell/sparse-package/AppxManifest.xml`. Install layout (planned WiX custom action):

```powershell
Add-AppxPackage -Path UltraCompress.Shell.msix -ExternalLocation "C:\Program Files\UltraCompress\Shell"
```

Publisher in the manifest is a placeholder (`CN=UltraCompress`). Production installs must match the **Authenticode** subject on the signed package.

## Dev test without Explorer

```csharp
await ShellExtensionStub.InvokeExplorerCommandAsync(
    new[] { @"C:\temp\sample.bin" },
    CompressionProfile.Small,
    queueDirectory: @"C:\temp\uc-queue-test");
```

Each invocation appends one JSON line to `inbox.jsonl` in the queue directory.

## Related

- [OPERATOR_SIGNING.md](./OPERATOR_SIGNING.md) — certificates required before registering the sparse package
- Blueprint §5.3 and internal windows-integration research (sparse package + `IExplorerCommand`)