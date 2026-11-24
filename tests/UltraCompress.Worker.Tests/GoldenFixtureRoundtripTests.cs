using UltraCompress.Core;
using UltraCompress.Core.SevenZip;
using UltraCompress.Worker.Backends;

namespace UltraCompress.Worker.Tests;

public sealed class GoldenFixtureRoundtripTests
{
    private static readonly string GoldenRoot = ResolveGoldenRoot();

    private static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<object[]> ZstdGoldenCases()
    {
        foreach (var relative in EnumerateFixtureRelativePaths())
        {
            yield return [relative, CreateZstdBackend(), DefaultZstdStep()];
        }
    }

    [Fact]
    public void SevenZip_IsAvailableInCi()
    {
        if (!IsCi)
        {
            return;
        }

        Assert.NotNull(SevenZipToolLocator.TryResolve());
    }

    [Theory]
    [MemberData(nameof(ZstdGoldenCases))]
    public async Task GoldenFixture_ZstdRoundtrip(string relativePath, ICompressionBackend backend, CompressStep step)
    {
        var original = await ReadFixtureAsync(relativePath);
        await AssertLosslessRoundtripAsync(backend, step, original);
    }

    [SkippableFact]
    public async Task GoldenFixture_SevenZipRoundtrip_AllFixtures()
    {
        var executable = SevenZipToolLocator.TryResolve();
        if (IsCi)
        {
            Assert.NotNull(executable);
        }

        Skip.If(executable is null, "7za not available (install p7zip or set ULTRACOMPRESS_7ZA_PATH).");

        var backend = new SevenZipProcessBackend(executable);
        var step = DefaultSevenZipStep();

        foreach (var relative in EnumerateFixtureRelativePaths())
        {
            var original = await ReadFixtureAsync(relative);
            await AssertLosslessRoundtripAsync(backend, step, original);
        }
    }

    private static async Task AssertLosslessRoundtripAsync(
        ICompressionBackend backend,
        CompressStep step,
        byte[] original)
    {
        var compressed = await backend.CompressAsync(original, step);
        Assert.NotEmpty(compressed);

        var restored = await backend.DecompressAsync(compressed, step);
        Assert.Equal(original, restored);
    }

    private static async Task<byte[]> ReadFixtureAsync(string relativePath)
    {
        var fullPath = Path.Combine(GoldenRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath), $"Missing golden fixture: {fullPath}");
        return await File.ReadAllBytesAsync(fullPath);
    }

    private static IEnumerable<string> EnumerateFixtureRelativePaths()
    {
        if (!Directory.Exists(GoldenRoot))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(GoldenRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(GoldenRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            yield return relative;
        }
    }

    private static string ResolveGoldenRoot()
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "golden-files");
        if (Directory.Exists(fromOutput))
        {
            return fromOutput;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "golden-files");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate tests/golden-files.");
    }

    private static ZstdCompressionBackend CreateZstdBackend() => new();

    private static CompressStep DefaultZstdStep() => new() { Codec = "zstd", Level = 3 };

    private static CompressStep DefaultSevenZipStep() => new()
    {
        Codec = "7z",
        Level = 5,
        Method = "LZMA2",
        Solid = false,
        Threads = 1,
    };
}