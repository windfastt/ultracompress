# Third-party notices

UltraCompress links to or subprocess-invokes the components below. Full license texts live beside bundled binaries where applicable.

## 7-Zip (LGPL v2.1+)

- **Components:** `7za.exe`, `7za.dll`, `7z.dll` (bundled under `tools/7z/` on Windows installs)
- **Use:** LZMA2 / 7z archive compress and decompress via isolated worker subprocess
- **License:** GNU Lesser General Public License v2.1 or later
- **Attribution:** `tools/7z/LICENSE-7zip.txt`, link to https://www.7-zip.org/
- **Source offer:** documented in `LICENSE-7zip.txt` and surfaced in Desktop **Settings → Legal**

## Zstandard (BSD)

- **Component:** ZstdSharp.Port / libzstd (in-process in worker)
- **License:** BSD License — https://github.com/facebook/zstd

## Brotli (MIT)

- **Component:** Brotli encoder/decoder in .NET 8 (`System.IO.Compression`)
- **License:** MIT — https://github.com/google/brotli

## libdeflate (MIT)

- **Component:** LibDeflate.NET / LibDeflate.Native (in-process raw DEFLATE)
- **License:** MIT — https://github.com/ebiggers/libdeflate

---

When adding codecs, append a section here and reference it from the installer “Open source licenses” page.