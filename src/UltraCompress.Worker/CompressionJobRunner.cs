using UltraCompress.Core;
using UltraCompress.Core.Advisor;
using UltraCompress.Worker.Backends;
using UltraCompress.Worker.Verification;

namespace UltraCompress.Worker;

public sealed class CompressionJobRunner
{
    private readonly IReadOnlyDictionary<string, ICompressionBackend> _backends;
    private readonly RoundtripVerifier _verifier = new();

    public CompressionJobRunner()
    {
        var zstd = new ZstdCompressionBackend();
        _backends = new Dictionary<string, ICompressionBackend>(StringComparer.OrdinalIgnoreCase)
        {
            [zstd.CodecName] = zstd,
        };
    }

    public async Task<CompressionJobResult> RunAsync(
        string inputPath,
        CompressionProfile profile,
        string? outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file not found.", inputPath);
        }

        var fileInfo = new FileInfo(inputPath);
        var fingerprint = new FileFingerprint
        {
            Path = Path.GetFullPath(inputPath),
            SizeBytes = fileInfo.Length,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
        };

        var recipe = PipelineAdvisor.SuggestPrimaryRecipe(fingerprint, profile);

        var inputBytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);

        var compressStep = recipe.Steps.OfType<CompressStep>().LastOrDefault()
            ?? throw new InvalidOperationException("Recipe has no compress step.");

        if (!_backends.TryGetValue(compressStep.Codec, out var backend))
        {
            throw new NotSupportedException($"Codec '{compressStep.Codec}' is not registered.");
        }

        var compressed = await backend.CompressAsync(inputBytes, compressStep, cancellationToken);
        var decompressed = await backend.DecompressAsync(compressed, compressStep, cancellationToken);
        var verification = _verifier.Verify(inputBytes, decompressed, recipe.Verify);

        outputPath ??= inputPath + ".uc.zst";
        await File.WriteAllBytesAsync(outputPath, compressed, cancellationToken);

        return new CompressionJobResult(
            fingerprint,
            recipe,
            outputPath,
            inputBytes.Length,
            compressed.Length,
            verification);
    }
}

public sealed record CompressionJobResult(
    FileFingerprint Fingerprint,
    PipelineRecipe Recipe,
    string OutputPath,
    long OriginalBytes,
    long CompressedBytes,
    VerificationResult Verification)
{
    public double Ratio => OriginalBytes == 0 ? 0 : (double)CompressedBytes / OriginalBytes;
}