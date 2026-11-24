using UltraCompress.Core;
using UltraCompress.Worker.Backends;
using UltraCompress.Worker.Verification;

namespace UltraCompress.Worker;

public sealed class CompressionJobRunner
{
    private readonly IReadOnlyDictionary<string, ICompressionBackend> _backends;
    private readonly RoundtripVerifier _verifier = new();

    public CompressionJobRunner(IReadOnlyDictionary<string, ICompressionBackend>? backends = null)
    {
        if (backends is not null)
        {
            _backends = backends;
            return;
        }

        var map = new Dictionary<string, ICompressionBackend>(StringComparer.OrdinalIgnoreCase);
        Register(map, new ZstdCompressionBackend());
        Register(map, new BrotliCompressionBackend());
        Register(map, new LibDeflateCompressionBackend());
        _backends = map;
    }

    private static void Register(Dictionary<string, ICompressionBackend> map, ICompressionBackend backend) =>
        map[backend.CodecName] = backend;

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

        var recipe = ProfileDefaults.CreateDefaultRecipe(profile);
        var fileInfo = new FileInfo(inputPath);
        var fingerprint = new FileFingerprint
        {
            Path = Path.GetFullPath(inputPath),
            SizeBytes = fileInfo.Length,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
        };

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

        outputPath ??= inputPath + GetDefaultOutputExtension(compressStep.Codec);
        await File.WriteAllBytesAsync(outputPath, compressed, cancellationToken);

        return new CompressionJobResult(
            fingerprint,
            recipe,
            outputPath,
            inputBytes.Length,
            compressed.Length,
            verification);
    }

    private static string GetDefaultOutputExtension(string codec) =>
        codec.ToLowerInvariant() switch
        {
            "brotli" => ".uc.br",
            "libdeflate" => ".uc.deflate",
            "7z" => ".uc.7z",
            _ => ".uc.zst",
        };
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