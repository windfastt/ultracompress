using UltraCompress.Core;
using UltraCompress.Core.Advisor;
using UltraCompress.Core.Recipes;
using UltraCompress.Core.Safety;
using UltraCompress.Core.Search;
using UltraCompress.Core.Transforms;
using UltraCompress.Worker.Backends;
using UltraCompress.Worker.Verification;

namespace UltraCompress.Worker;

public sealed class CompressionJobRunner
{
    private readonly IReadOnlyDictionary<string, ICompressionBackend> _backends;
    private readonly IRecipeStore? _recipeStore;
    private readonly TournamentCoordinator _tournament = new();

    public CompressionJobRunner(
        IReadOnlyDictionary<string, ICompressionBackend>? backends = null,
        IRecipeStore? recipeStore = null)
    {
        _recipeStore = recipeStore;
        if (backends is not null)
        {
            var copy = new Dictionary<string, ICompressionBackend>(backends, StringComparer.OrdinalIgnoreCase);
            copy.TryAdd(StoreCompressionBackend.Instance.CodecName, StoreCompressionBackend.Instance);
            _backends = copy;
            return;
        }

        var map = new Dictionary<string, ICompressionBackend>(StringComparer.OrdinalIgnoreCase);
        Register(map, StoreCompressionBackend.Instance);
        RegisterIfAvailable(map, new ZstdCompressionBackend());
        Register(map, new BrotliCompressionBackend());
        Register(map, new LibDeflateCompressionBackend());
        RegisterIfAvailable(map, TryCreateSevenZipBackend());
        _backends = map;
    }

    public IReadOnlyDictionary<string, ICompressionBackend> Backends => _backends;

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

        var contract = ProfileContract.Get(profile);
        contract.ValidateContractInvariants();
        MutatingOperationGuard.EnforceProfileBoundary(profile, OperationKind.LosslessCompression);
        MutatingOperationGuard.Enforce(
            OperationKind.LosslessCompression,
            consentToken: null,
            sourcePath: inputPath,
            destinationPath: outputPath);

        var fingerprint = await FileProbe.ProbeAsync(inputPath, cancellationToken: cancellationToken);
        var inputBytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);

        // 1. Check pinned/cached recipe override if a store is attached and matches current file state.
        if (_recipeStore is not null && _recipeStore.TryResolveRecipe(fingerprint, out var pinnedOrCached) && pinnedOrCached is not null)
        {
            SafetyPolicy.ValidateCompressionProfileRecipe(profile, pinnedOrCached);
            return await ExecuteSingleRecipeAndWriteAsync(
                fingerprint,
                inputPath,
                inputBytes,
                profile,
                contract,
                pinnedOrCached,
                outputPath,
                tournamentOutcome: null,
                cancellationToken);
        }

        // 2. Multi-candidate UCB1 + successive-halving tournament for Tiny & Laboratory.
        if (contract.UsesMultiCandidateTournament)
        {
            SqliteRecipeStore? ownedDefaultStore = null;
            var activeStore = _recipeStore;
            if (activeStore is null)
            {
                ownedDefaultStore = SqliteRecipeStore.OpenDefault();
                activeStore = ownedDefaultStore;
            }

            try
            {
                var tournamentOutcome = await _tournament.ExecuteProductionTournamentAsync(
                    fingerprint,
                    inputBytes,
                    profile,
                    _backends,
                    activeStore,
                    TournamentOptions.ForProfile(profile),
                    cancellationToken);

                var finalCodec = tournamentOutcome.WinningRecipe.Steps.OfType<CompressStep>().Last().Codec;
                var resolvedOutput = outputPath ?? (inputPath + GetDefaultOutputExtension(finalCodec));
                MutatingOperationGuard.Enforce(
                    OperationKind.LosslessCompression,
                    consentToken: null,
                    sourcePath: inputPath,
                    destinationPath: resolvedOutput);

                await File.WriteAllBytesAsync(resolvedOutput, tournamentOutcome.CompressedBytes, cancellationToken);

                return new CompressionJobResult(
                    fingerprint,
                    tournamentOutcome.WinningRecipe,
                    resolvedOutput,
                    inputBytes.Length,
                    tournamentOutcome.CompressedBytes.Length,
                    RoundtripVerifier.FromBitExact(tournamentOutcome.Verification),
                    tournamentOutcome);
            }
            finally
            {
                ownedDefaultStore?.Dispose();
            }
        }

        // 3. Single-pass profiles (Instant, Balanced, Archive) with contract entropy bypass and mandatory bit-exact verify.
        PipelineRecipe selectedRecipe;
        if (ShouldBypassToStore(inputBytes, fingerprint, contract))
        {
            selectedRecipe = ProfileDefaults.CreateStoreRecipe(profile);
        }
        else
        {
            selectedRecipe = PipelineAdvisor.SuggestPrimaryRecipe(fingerprint, profile);
            var primaryCodec = selectedRecipe.Steps.OfType<CompressStep>().Last().Codec;
            if (!_backends.ContainsKey(primaryCodec))
            {
                selectedRecipe = ProfileDefaults.CreateDefaultRecipe(CompressionProfile.Balanced);
            }
        }

        SafetyPolicy.ValidateCompressionProfileRecipe(profile, selectedRecipe);
        return await ExecuteSingleRecipeAndWriteAsync(
            fingerprint,
            inputPath,
            inputBytes,
            profile,
            contract,
            selectedRecipe,
            outputPath,
            tournamentOutcome: null,
            cancellationToken);
    }

    public async Task<byte[]> DecompressAsync(
        ReadOnlyMemory<byte> compressedBytes,
        PipelineRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var compressStep = recipe.Steps.OfType<CompressStep>().LastOrDefault()
            ?? throw new InvalidOperationException("Recipe has no compress step.");

        if (!_backends.TryGetValue(compressStep.Codec, out var backend))
        {
            throw new NotSupportedException($"Codec '{compressStep.Codec}' is not registered.");
        }

        var decompressed = await backend.DecompressAsync(compressedBytes, compressStep, cancellationToken);
        return TransformPipeline.Decode(decompressed, recipe.Steps);
    }

    private async Task<CompressionJobResult> ExecuteSingleRecipeAndWriteAsync(
        FileFingerprint fingerprint,
        string inputPath,
        byte[] inputBytes,
        CompressionProfile profile,
        ProfileContract contract,
        PipelineRecipe recipe,
        string? outputPath,
        TournamentExecutionResult? tournamentOutcome,
        CancellationToken cancellationToken)
    {
        var compressStep = recipe.Steps.OfType<CompressStep>().LastOrDefault()
            ?? throw new InvalidOperationException("Recipe has no compress step.");

        if (!_backends.TryGetValue(compressStep.Codec, out var backend))
        {
            throw new NotSupportedException($"Codec '{compressStep.Codec}' is not registered.");
        }

        var transformed = TransformPipeline.Encode(inputBytes, recipe.Steps);
        var compressed = await backend.CompressAsync(transformed, compressStep, cancellationToken);

        // Switch to Store if incompressible on non-trivial payloads so output never expands needlessly.
        if (!compressStep.Codec.Equals("store", StringComparison.OrdinalIgnoreCase)
            && inputBytes.Length >= 512
            && (double)compressed.Length / inputBytes.Length >= contract.IncompressibleRatioThreshold)
        {
            recipe = ProfileDefaults.CreateStoreRecipe(profile);
            compressStep = recipe.Steps.OfType<CompressStep>().Last();
            backend = StoreCompressionBackend.Instance;
            compressed = await backend.CompressAsync(inputBytes, compressStep, cancellationToken);
        }

        var decompressed = await backend.DecompressAsync(compressed, compressStep, cancellationToken);
        var restored = TransformPipeline.Decode(decompressed, recipe.Steps);
        var bitExact = BitExactVerifier.VerifyOrThrow(inputBytes, restored);

        var resolvedOutput = outputPath ?? (inputPath + GetDefaultOutputExtension(compressStep.Codec));
        MutatingOperationGuard.Enforce(
            OperationKind.LosslessCompression,
            consentToken: null,
            sourcePath: inputPath,
            destinationPath: resolvedOutput);

        await File.WriteAllBytesAsync(resolvedOutput, compressed, cancellationToken);

        return new CompressionJobResult(
            fingerprint,
            recipe,
            resolvedOutput,
            inputBytes.Length,
            compressed.Length,
            RoundtripVerifier.FromBitExact(bitExact),
            tournamentOutcome);
    }

    private static bool ShouldBypassToStore(
        ReadOnlySpan<byte> inputBytes,
        FileFingerprint fingerprint,
        ProfileContract contract)
    {
        if (inputBytes.IsEmpty)
        {
            return true;
        }

        if (fingerprint.EntropyBitsPerByte is not double rawEntropy
            || rawEntropy < contract.IncompressibleEntropyBypassBitsPerByte)
        {
            return false;
        }

        var (_, deltaEntropy) = DeltaTransform.SelectBestStride(inputBytes);
        return deltaEntropy >= contract.IncompressibleEntropyBypassBitsPerByte;
    }

    private static void RegisterIfAvailable(Dictionary<string, ICompressionBackend> map, ICompressionBackend? backend)
    {
        if (backend is not null)
        {
            map[backend.CodecName] = backend;
        }
    }

    private static void Register(Dictionary<string, ICompressionBackend> map, ICompressionBackend backend) =>
        map[backend.CodecName] = backend;

    private static SevenZipProcessBackend? TryCreateSevenZipBackend()
    {
        try
        {
            return new SevenZipProcessBackend();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static string GetDefaultOutputExtension(string codec) =>
        codec.ToLowerInvariant() switch
        {
            "store" => ".uc.raw",
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
    VerificationResult Verification,
    TournamentExecutionResult? Tournament = null)
{
    public double Ratio => OriginalBytes == 0 ? 0 : (double)CompressedBytes / OriginalBytes;
}
