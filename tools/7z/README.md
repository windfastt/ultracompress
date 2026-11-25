# Bundled 7-Zip command-line tools

UltraCompress ships the **7-Zip standalone command-line** binaries here for LGPL compliance and deterministic worker behavior.

## What to place in this folder (Windows installer)

| File | Source |
|------|--------|
| `7za.exe` | [7-Zip Extra](https://www.7-zip.org/sdk.html) package |
| `7za.dll` | Same package (if present) |
| `7z.dll` | Same package |

**Do not commit** `.exe` / `.dll` binaries to this git repository. CI and dev machines may use **p7zip** (`7za` on `PATH`) or set `ULTRACOMPRESS_7ZA_PATH`.

Typical install layout after WiX / setup:

```text
Program Files\UltraCompress\
  tools\7z\7za.exe
  tools\7z\7z.dll
  tools\7z\LICENSE-7zip.txt
```

## Runtime resolution order

1. Environment variable `ULTRACOMPRESS_7ZA_PATH` (full path to `7za` / `7za.exe`)
2. `<install-root>\tools\7z\7za.exe` (or `7za` on Unix)
3. `7za` on the process `PATH` (development / Linux CI)

## Legal

7-Zip is **LGPL**. See:

- [`LICENSE-7zip.txt`](LICENSE-7zip.txt) in this directory
- [`docs/legal/THIRD_PARTY_NOTICES.md`](../../docs/legal/THIRD_PARTY_NOTICES.md)
- Desktop **Settings → Legal** surfaces the same third-party notices

**Corresponding source:** obtain 7-Zip source from https://www.7-zip.org/download.html or https://sourceforge.net/projects/sevenzip/ — offer language is in `LICENSE-7zip.txt`.

## Usage in recipes

Worker subprocess adapter: `SevenZipProcessBackend` (`codec: "7z"`). Example DSL step:

```json
{ "op": "compress", "codec": "7z", "method": "LZMA2", "level": 9, "solid": false, "threads": 0 }
```

The **Laboratory** profile defaults to a single-file 7z archive with LZMA2 at `-mx=9`.