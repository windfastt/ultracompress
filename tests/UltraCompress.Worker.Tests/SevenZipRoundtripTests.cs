using UltraCompress.Core;
using UltraCompress.Core.SevenZip;
using UltraCompress.Worker.Backends;

namespace UltraCompress.Worker.Tests;

public class SevenZipRoundtripTests
{
    [SkippableFact]
    public async Task SevenZipProcessBackend_Roundtrip_PreservesPayload()
    {
        var executable = SevenZipToolLocator.TryResolve();
        Skip.If(executable is null, "7za not available (install p7zip or set ULTRACOMPRESS_7ZA_PATH).");

        var backend = new SevenZipProcessBackend(executable);
        var step = new CompressStep
        {
            Codec = "7z",
            Level = 5,
            Method = "LZMA2",
            Solid = false,
            Threads = 1,
        };

        var payload = "UltraCompress golden payload — 7za subprocess adapter.\n"u8.ToArray();
        var archive = await backend.CompressAsync(payload, step);
        Assert.NotEmpty(archive);

        var restored = await backend.DecompressAsync(archive, step);
        Assert.Equal(payload, restored);
    }
}