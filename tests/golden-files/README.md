# Golden compression fixtures

Checked-in corpora for **lossless compress → decompress roundtrip** tests across Worker codec backends.

| Path | Intent |
|------|--------|
| `text/ascii-repeated.txt` | Repetitive ASCII (dictionary-friendly) |
| `text/unicode-sample.txt` | UTF-8 with multiple scripts |
| `binary/structured-zeros.bin` | Printable run + NUL padding |
| `mixed/ndjson-lines.ndjson` | Structured text / logs |

Tests live in `UltraCompress.Worker.Tests` (`GoldenFixtureRoundtripTests`). Do not recompress these files in-place; add new fixtures when expanding coverage.