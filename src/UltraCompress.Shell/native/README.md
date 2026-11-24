# Native `IExplorerCommand` (not built in CI yet)

Windows 11 registers Explorer commands through the sparse package manifest in `../sparse-package/`. The surrogate DLL (`UltraCompress.ShellStub.dll`) must:

1. Implement `IExplorerCommand` (and factory `DllGetClassObject`).
2. On `Invoke`, collect selected shell items and call `ShellEnqueueClient` via IPC or write the same JSONL format as the managed stub.
3. Stay fast on `GetTitle` / `GetIcon` / `GetState` — never compress inside Explorer's process.

Build with Visual Studio 2022 (Desktop development with C++, ATL). Sign the DLL and the sparse `.msix` before registration on 24H2+.

See [docs/SHELL_EXTENSION.md](../../../docs/SHELL_EXTENSION.md) and [docs/OPERATOR_SIGNING.md](../../../docs/OPERATOR_SIGNING.md).