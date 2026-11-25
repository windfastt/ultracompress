# Windows Explorer Context-Menu Integration

UltraCompress integrates with Windows 10/11 File Explorer so users can right-click files or folders and enqueue compression jobs directly into `UltraCompress.Engine` without blocking the Explorer shell process.

## Architecture

```text
Windows Explorer  →  IExplorerCommand (Sparse MSIX Identity)
                  →  ShellEnqueueClient
                  →  \\.\pipe\UltraCompress.Engine (fallback: %ProgramData%\UltraCompress\queue\inbox.jsonl)
                  →  UltraCompress.Engine Scheduler & Worker Processes
```

Shell handlers perform zero in-process compression inside `explorer.exe`; they strictly validate paths, attach the selected lossless `CompressionProfile`, and hand off execution to the background engine.

## Sparse Package Registration

The sparse package manifest lives at `src/UltraCompress.Shell/sparse-package/AppxManifest.xml`. During signed production installation:

```powershell
Add-AppxPackage -Path UltraCompress.Shell.msix -ExternalLocation "C:\Program Files\UltraCompress\Shell"
```

Windows 11 requires sparse packages to be signed with a trusted Authenticode certificate whose subject matches `Identity/@Publisher` in `AppxManifest.xml`. See [OPERATOR_SIGNING.md](./OPERATOR_SIGNING.md) for the release signing checklist.

## Programmatic & Integration Testing

`ShellEnqueueClient` and `ShellExtensionStub` allow deterministic automated testing on CI without interactive Explorer shell sessions:

```csharp
await ShellExtensionStub.InvokeExplorerCommandAsync(
    new[] { @"C:\Workloads\dataset.sql" },
    CompressionProfile.Balanced,
    queueDirectory: @"C:\temp\uc-queue-test");
```

## Related

- [OPERATOR_SIGNING.md](./OPERATOR_SIGNING.md) — Authenticode signing requirements for sparse MSIX registration
- [USER.md](./USER.md) — Desktop, CLI, and Explorer usage guide
