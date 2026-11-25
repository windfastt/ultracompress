# Operator signing requirements (UltraCompress)

CI produces **unsigned** binaries. Shipping the tray, engine, installer, or **Explorer shell extension** on end-user machines requires an operator-controlled code-signing certificate. This document is the operator checklist; it does not store secrets.

## What must be signed

| Artifact | Why |
|----------|-----|
| `UltraCompress.Shell.msix` (sparse package) | `Add-AppxPackage` and Explorer ignore untrusted sparse packages on current Windows builds |
| `UltraCompress.ShellStub.dll` (future native COM) | Windows 11 24H2+ tightens DLL load policy for shell extensions |
| `UltraCompress.Engine.exe`, `UltraCompress.Tray.exe`, `UltraCompress.Worker.exe` | SmartScreen and enterprise App Control |
| WiX **Bundle** `.exe` and inner **MSI** | Trusted install/upgrade chain |
| VC++ redist bootstrap (optional) | Only if you repackage; Microsoft blobs are already signed |

Sign every PE you ship with **SHA-256** file digest and an **RFC 3161 timestamp** ([SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/using-signtool-to-sign-a-file)).

## Certificate types

| Environment | Typical cert |
|-------------|----------------|
| Local dev / VM | Self-signed code-signing cert installed to **LocalMachine\Root** (PowerToys-style); sufficient for one machine |
| Production / release | **Standard** or **EV** code signing from a Microsoft Trusted Root CA member |

Sparse package `Identity/@Publisher` must match the signing subject (or your org's publish pipeline updates the manifest per build).

## Operator workflow (release)

1. Obtain or renew the code-signing certificate (store private key in HSM or secure vault; never commit to git).
2. Build Release artifacts on a trusted host (`dotnet publish`, WiX bundle).
3. Sign native shell DLL **before** packing MSIX; sign the `.msix` payload.
4. Sign installer bundle, then verify:

   ```text
   signtool verify /pa /v UltraCompress.Setup.exe
   ```

5. Register sparse package only on signed bits; WiX deferred action should no-op with a clear log when signature missing (future installer work).
6. Archive thumbprint, cert expiry, and build ID in your release notes.

## CI vs operator

| | CI (GitHub Actions) | Operator release |
|--|---------------------|------------------|
| Signing | None | Required for shell + installer |
| Explorer menu | Not registered | After signed sparse install |
| SmartScreen | Expected warnings on unsigned EXE | Mitigated after cert reputation |

## Uninstall

Remove sparse package by family name (`UltraCompress.Shell_*`), remove signed DLLs under `Program Files\UltraCompress\Shell`, and leave `%ProgramData%\UltraCompress\recipes.db` per user prompt (installer spec).

## Questions / queue

If no production certificate is available yet, keep shell registration disabled and use `ShellExtensionStub` dev enqueue only. Document release blockers in the project operator queue.