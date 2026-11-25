using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using UltraCompress.Core;
using UltraCompress.Core.Recipes;
using UltraCompress.Core.Search;
using UltraCompress.Worker;
using UltraCompress.Worker.Backends;

namespace UltraCompress.SafetyAndProfiles.Tests;

public sealed class ProductionTournamentAndEndToEndTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _recipeDbPath;
    private readonly SqliteRecipeStore _recipeStore;

    public ProductionTournamentAndEndToEndTests()
    {
        _tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"uc-track1-{Guid.NewGuid():N}")).FullName;
        _recipeDbPath = Path.Combine(_tempDir, "recipes.db");
        _recipeStore = new SqliteRecipeStore(_recipeDbPath);
        _recipeStore.EnsureInitialized();
    }

    public void Dispose()
    {
        _recipeStore.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(CompressionProfile.Tiny)]
    [InlineData(CompressionProfile.Laboratory)]
    public void TournamentArmCatalog_CoversFullFourByFourPreprocessorAndCodecMatrix(CompressionProfile profile)
    {
        var fp = new FileFingerprint
        {
            Path = Path.Combine(_tempDir, "matrix.bin"),
            SizeBytes = 128 * 1024,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            EntropyBitsPerByte = 5.0,
        };

        var allFourCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "zstd",
            "brotli",
            "libdeflate",
            "7z",
        };

        var arms = PipelineArmCatalog.SelectArms(fp, profile, availableCodecs: allFourCodecs);
        Assert.True(arms.Count >= 16);

        var preprocessors = arms
            .Select(a => a.Recipe.Steps.OfType<PreprocessStep>().First().Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codecs = arms
            .Select(a => a.Recipe.Steps.OfType<CompressStep>().Last().Codec)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("raw", preprocessors);
        Assert.Contains("delta", preprocessors);
        Assert.Contains("byteshuffle", preprocessors);
        Assert.Contains("x86_bcj", preprocessors);

        Assert.Contains("zstd", codecs);
        Assert.Contains("brotli", codecs);
        Assert.Contains("libdeflate", codecs);
        Assert.Contains("7z", codecs);
    }

    [Fact]
    public async Task Tournament_SelectsStructuredPreprocessorWinner_VerifiesBitExact_AndRecordsInSqliteRecipeStore()
    {
        // Structured 8-byte sensor/timeseries dump (96 KiB > Tiny's 64 KiB pilot sample so multi-window pilot runs!)
        var payload = new byte[12_288 * 8];
        for (var i = 0; i < 12_288; i++)
        {
            var val = 500_000_000_000L + i * 16L + (i % 3);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(i * 8, 8), val);
        }

        var inputPath = Path.Combine(_tempDir, "timeseries.bin");
        await File.WriteAllBytesAsync(inputPath, payload);
        var fp = await FileProbe.ProbeAsync(inputPath);

        var runner = new CompressionJobRunner(recipeStore: _recipeStore);
        var coordinator = new TournamentCoordinator();

        var outcome = await coordinator.ExecuteProductionTournamentAsync(
            fp,
            payload,
            CompressionProfile.Tiny,
            runner.Backends,
            _recipeStore);

        Assert.False(outcome.UsedStoreFallback);
        Assert.True(outcome.Verification.Passed);
        Assert.Equal(fp.ContentHashSha256, outcome.Verification.OriginalSha256);
        Assert.True(outcome.RungsExecuted >= 2, "Expected multi-rung successive halving on 96 KiB payload.");
        Assert.Equal(ProfileContract.Get(CompressionProfile.Tiny).PilotSampleBytes, outcome.PilotSampleBytesEvaluated);

        var winningPreprocess = outcome.WinningRecipe.Steps.OfType<PreprocessStep>().First().Name;
        Assert.True(
            winningPreprocess is "delta" or "byteshuffle",
            $"Expected delta or byteshuffle to win on 8-byte series, got '{winningPreprocess}'.");

        // Compare against raw zstd-19 to confirm tournament winner genuinely beats raw compression
        var rawZstd = await new ZstdCompressionBackend().CompressAsync(payload, new CompressStep { Codec = "zstd", Level = 19 });
        Assert.True(
            outcome.CompressedBytes.Length < rawZstd.Length,
            $"Tournament winner ({outcome.CompressedBytes.Length} B) should beat Raw+Zstd19 ({rawZstd.Length} B).");

        // Verify full roundtrip restoration via runner.DecompressAsync
        var restored = await runner.DecompressAsync(outcome.CompressedBytes, outcome.WinningRecipe);
        Assert.Equal(payload, restored);

        // Verify winning recipe was recorded in SqliteRecipeStore and resolves with intact stride params
        Assert.True(_recipeStore.TryResolveRecipe(fp, out var storedRecipe));
        Assert.NotNull(storedRecipe);
        Assert.Equal(outcome.WinningRecipe.Id, storedRecipe!.Id);

        var restoredViaStoredRecipe = await runner.DecompressAsync(outcome.CompressedBytes, storedRecipe);
        Assert.Equal(payload, restoredViaStoredRecipe);
    }

    [Fact]
    public async Task Tournament_IncompressibleRandomStream_BypassesToStore_AndRecordsStoreRecipe()
    {
        var randomPayload = RandomNumberGenerator.GetBytes(80 * 1024);
        var inputPath = Path.Combine(_tempDir, "encrypted.bin");
        await File.WriteAllBytesAsync(inputPath, randomPayload);
        var fp = await FileProbe.ProbeAsync(inputPath);

        var runner = new CompressionJobRunner(recipeStore: _recipeStore);
        var jobResult = await runner.RunAsync(inputPath, CompressionProfile.Laboratory, outputPath: null);

        Assert.True(jobResult.Verification.Passed);
        Assert.NotNull(jobResult.Verification.OriginalSha256);
        Assert.NotNull(jobResult.Verification.OriginalXxHash64);
        Assert.NotNull(jobResult.Tournament);
        Assert.True(jobResult.Tournament!.UsedStoreFallback);

        var compressStep = jobResult.Recipe.Steps.OfType<CompressStep>().Last();
        Assert.Equal("store", compressStep.Codec);
        Assert.Equal(randomPayload.Length, jobResult.CompressedBytes);

        var diskCompressed = await File.ReadAllBytesAsync(jobResult.OutputPath);
        var restored = await runner.DecompressAsync(diskCompressed, jobResult.Recipe);
        Assert.Equal(randomPayload, restored);

        Assert.True(_recipeStore.TryResolveRecipe(fp, out var cached));
        Assert.Equal("store", cached!.Steps.OfType<CompressStep>().Last().Codec);
    }

    [Fact]
    public async Task Tournament_SelectsStore_WhenBestCandidateRatioIsAtOrAbove0995_EvenBelowEntropyGate()
    {
        // Moderate-entropy payload (< 7.95 bpb so entropy gate does not short-circuit) with mock backend returning >= 0.996x
        var payload = new byte[8192];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 64); // entropy = 6.0 bpb < 7.95
        }

        var fp = new FileFingerprint
        {
            Path = Path.Combine(_tempDir, "lowgain.bin"),
            SizeBytes = payload.Length,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            ContentHashSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            EntropyBitsPerByte = 6.0,
        };

        var nearOneBackend = new FixedOverheadMockBackend("zstd");
        var backends = new Dictionary<string, ICompressionBackend>(StringComparer.OrdinalIgnoreCase)
        {
            ["store"] = StoreCompressionBackend.Instance,
            ["zstd"] = nearOneBackend,
        };

        var coordinator = new TournamentCoordinator();
        var outcome = await coordinator.ExecuteProductionTournamentAsync(
            fp,
            payload,
            CompressionProfile.Tiny,
            backends,
            _recipeStore);

        Assert.True(outcome.UsedStoreFallback);
        Assert.Equal(TournamentStopReason.IncompressibleBypass, outcome.StopReason);
        Assert.Equal("store", outcome.WinningRecipe.Steps.OfType<CompressStep>().Last().Codec);
        Assert.Equal(payload, outcome.CompressedBytes);
        Assert.True(outcome.Verification.Passed);
    }

    [Theory]
    [InlineData(CompressionProfile.Instant)]
    [InlineData(CompressionProfile.Balanced)]
    [InlineData(CompressionProfile.Archive)]
    [InlineData(CompressionProfile.Tiny)]
    [InlineData(CompressionProfile.Laboratory)]
    public async Task AllFiveProfiles_EndToEndRoundtripBitExact_AcrossX86TabularTextAndRandomWorkloads(CompressionProfile profile)
    {
        var runner = new CompressionJobRunner(recipeStore: _recipeStore);

        var workloads = new Dictionary<string, byte[]>
        {
            [$"{profile}-source.cs"] = System.Text.Encoding.UTF8.GetBytes(
                string.Concat(Enumerable.Repeat("public sealed record Sample(int Id, string Name, long TimestampUtc);\n", 300))),
            [$"{profile}-native.exe"] = LosslessTransformsTests.BuildSyntheticX86ExecutableSection(32_768),
            [$"{profile}-metrics.sqlite"] = CreateTabularInt32Payload(32_768),
            [$"{profile}-random.mp4"] = RandomNumberGenerator.GetBytes(16_384),
        };

        foreach (var (fileName, expectedBytes) in workloads)
        {
            var inPath = Path.Combine(_tempDir, fileName);
            var outPath = Path.Combine(_tempDir, fileName + ".out");
            await File.WriteAllBytesAsync(inPath, expectedBytes);

            var result = await runner.RunAsync(inPath, profile, outPath);

            Assert.True(result.Verification.Passed, $"{profile}/{fileName}: {result.Verification.Message}");
            Assert.NotNull(result.Verification.OriginalSha256);
            Assert.NotNull(result.Verification.OriginalXxHash64);

            var writtenBytes = await File.ReadAllBytesAsync(result.OutputPath);
            var restored = await runner.DecompressAsync(writtenBytes, result.Recipe);
            Assert.Equal(expectedBytes, restored);

            // Original source file must remain completely untouched
            Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(inPath));
        }
    }

    private static byte[] CreateTabularInt32Payload(int sizeBytes)
    {
        var buf = new byte[sizeBytes];
        for (var i = 0; i + 4 <= sizeBytes; i += 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(i, 4), 100_000 + (i / 4) * 3);
        }

        return buf;
    }

    private sealed class FixedOverheadMockBackend(string codecName) : ICompressionBackend
    {
        public string CodecName => codecName;

        public Task<byte[]> CompressAsync(ReadOnlyMemory<byte> input, CompressStep step, CancellationToken cancellationToken = default)
        {
            // Simulates 0.998 ratio (>= 0.995 threshold) while remaining lossless
            var output = new byte[input.Length + 1];
            output[0] = 0xAA;
            input.Span.CopyTo(output.AsSpan(1));
            return Task.FromResult(output);
        }

        public Task<byte[]> DecompressAsync(ReadOnlyMemory<byte> compressed, CompressStep step, CancellationToken cancellationToken = default)
        {
            if (compressed.IsEmpty || compressed.Span[0] != 0xAA)
            {
                throw new InvalidDataException("Invalid mock header.");
            }

            return Task.FromResult(compressed.Span[1..].ToArray());
        }
    }
}
