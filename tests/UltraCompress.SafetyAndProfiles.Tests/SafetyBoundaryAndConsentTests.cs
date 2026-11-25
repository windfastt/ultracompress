using System.Text;
using UltraCompress.Core;
using UltraCompress.Core.Safety;
using UltraCompress.Core.Search;

namespace UltraCompress.SafetyAndProfiles.Tests;

public sealed class SafetyBoundaryAndConsentTests
{
    [Fact]
    public void CompressionProfile_DefinesStrictlyFiveCanonicalLosslessLevels()
    {
        var values = Enum.GetValues<CompressionProfile>();
        Assert.Equal(
            [
                CompressionProfile.Instant,
                CompressionProfile.Balanced,
                CompressionProfile.Archive,
                CompressionProfile.Tiny,
                CompressionProfile.Laboratory,
            ],
            values);

        foreach (var profile in values)
        {
            Assert.Equal(OperationKind.LosslessCompression, SafetyPolicy.GetOperationKind(profile));
        }
    }

    [Fact]
    public void OperationKind_DefinesStrictFourModes()
    {
        var modes = Enum.GetValues<OperationKind>();
        Assert.Equal(
            [
                OperationKind.LosslessCompression,
                OperationKind.LossyOptimization,
                OperationKind.FormatConversion,
                OperationKind.DataRemoval,
            ],
            modes);
    }

    [Theory]
    [InlineData(CompressionProfile.Instant)]
    [InlineData(CompressionProfile.Balanced)]
    [InlineData(CompressionProfile.Archive)]
    [InlineData(CompressionProfile.Tiny)]
    [InlineData(CompressionProfile.Laboratory)]
    public void AllProfiles_RejectMutatingOperationKindsAndLossyTransforms(CompressionProfile profile)
    {
        var validRecipe = ProfileDefaults.CreateDefaultRecipe(profile);
        SafetyPolicy.ValidateCompressionProfileRecipe(profile, validRecipe);

        foreach (var mutating in new[]
                 {
                     OperationKind.LossyOptimization,
                     OperationKind.FormatConversion,
                     OperationKind.DataRemoval,
                 })
        {
            Assert.Throws<SafetyPolicyViolationException>(
                () => MutatingOperationGuard.EnforceProfileBoundary(profile, mutating));
            Assert.Throws<SafetyPolicyViolationException>(
                () => SafetyPolicy.ValidateCompressionProfileRecipe(profile, validRecipe, mutating));
        }

        foreach (var bannedPreprocess in new[]
                 {
                     "png_palette_quant",
                     "jpeg_recompress",
                     "pdf_recompress_images",
                     "pdf_object_stream",
                     "sqlite_vacuum_pages",
                     "pe_strip_debug",
                     "exif_strip",
                     "unknown_custom_mutator",
                 })
        {
            var badRecipe = new PipelineRecipe
            {
                Id = "bad",
                Profile = profile,
                Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = true },
                Steps =
                [
                    new PreprocessStep { Name = bannedPreprocess },
                    new CompressStep { Codec = "zstd", Level = 3 },
                ],
            };

            Assert.Throws<SafetyPolicyViolationException>(
                () => SafetyPolicy.ValidateCompressionProfileRecipe(profile, badRecipe));
        }
    }

    [Theory]
    [InlineData(CompressionProfile.Instant)]
    [InlineData(CompressionProfile.Balanced)]
    [InlineData(CompressionProfile.Archive)]
    [InlineData(CompressionProfile.Tiny)]
    [InlineData(CompressionProfile.Laboratory)]
    public void AllProfiles_RequireMandatoryBitExactVerificationFlags(CompressionProfile profile)
    {
        var noRoundtrip = new PipelineRecipe
        {
            Id = "no-rt",
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = false, HashOriginal = true },
            Steps = [new CompressStep { Codec = "zstd", Level = 3 }],
        };

        var noHash = new PipelineRecipe
        {
            Id = "no-hash",
            Profile = profile,
            Verify = new VerifyOptions { DecompressRoundtrip = true, HashOriginal = false },
            Steps = [new CompressStep { Codec = "zstd", Level = 3 }],
        };

        Assert.Throws<SafetyPolicyViolationException>(() => SafetyPolicy.ValidateCompressionProfileRecipe(profile, noRoundtrip));
        Assert.Throws<SafetyPolicyViolationException>(() => SafetyPolicy.ValidateCompressionProfileRecipe(profile, noHash));
    }

    [Theory]
    [InlineData(CompressionProfile.Archive)]
    [InlineData(CompressionProfile.Tiny)]
    [InlineData(CompressionProfile.Laboratory)]
    public void PipelineArmCatalog_ContainsZeroLossyOrMutatingTransforms(CompressionProfile profile)
    {
        var fp = new FileFingerprint
        {
            Path = "dataset.bin",
            SizeBytes = 128 * 1024,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            EntropyBitsPerByte = 5.2,
        };

        var arms = PipelineArmCatalog.SelectArms(fp, profile);
        Assert.NotEmpty(arms);

        foreach (var arm in arms)
        {
            SafetyPolicy.ValidateCompressionProfileRecipe(profile, arm.Recipe);
            foreach (var pre in arm.Recipe.Steps.OfType<PreprocessStep>())
            {
                Assert.True(SafetyPolicy.IsAllowedLosslessPreprocessor(pre.Name), $"Unexpected preprocessor {pre.Name}");
            }
        }
    }

    [Theory]
    [InlineData(OperationKind.LossyOptimization)]
    [InlineData(OperationKind.FormatConversion)]
    [InlineData(OperationKind.DataRemoval)]
    public void MutatingOperationGuard_ThrowsConsentRequired_WhenTokenMissingOrIncomplete(OperationKind kind)
    {
        var validPreview = CreatePreview(kind, "/tmp/photo.jpg");
        var validParams = new Dictionary<string, string> { ["quality"] = "85" };

        // 1. Null token
        var exNull = Assert.Throws<SafetyConsentRequiredException>(() => MutatingOperationGuard.Enforce(kind));
        Assert.Equal(kind, exNull.OperationKind);

        // 2. Mismatched OperationKind
        var wrongKind = kind == OperationKind.LossyOptimization ? OperationKind.DataRemoval : OperationKind.LossyOptimization;
        var tokenWrongKind = OperationConsentToken.Create(wrongKind, validParams, CreatePreview(wrongKind, "/tmp/photo.jpg"));
        Assert.Throws<SafetyConsentRequiredException>(() => MutatingOperationGuard.Enforce(kind, tokenWrongKind));

        // 3. Non-reversibility not acknowledged
        var tokenUnack = OperationConsentToken.Create(kind, validParams, validPreview, acknowledgeNonReversible: false);
        Assert.Throws<SafetyConsentRequiredException>(() => MutatingOperationGuard.Enforce(kind, tokenUnack));

        // 4. Empty target parameters
        var tokenNoParams = OperationConsentToken.Create(kind, new Dictionary<string, string>(), validPreview);
        Assert.Throws<SafetyConsentRequiredException>(() => MutatingOperationGuard.Enforce(kind, tokenNoParams));

        // 5. Empty itemized impact preview
        var emptyPreview = new OperationImpactPreview
        {
            OperationKind = kind,
            Summary = "Empty preview",
            Items = Array.Empty<ImpactPreviewItem>(),
        };
        var tokenEmptyItems = OperationConsentToken.Create(kind, validParams, emptyPreview);
        Assert.Throws<SafetyConsentRequiredException>(() => MutatingOperationGuard.Enforce(kind, tokenEmptyItems));
    }

    [Theory]
    [InlineData(OperationKind.LossyOptimization)]
    [InlineData(OperationKind.FormatConversion)]
    [InlineData(OperationKind.DataRemoval)]
    public void MutatingOperationGuard_EnforcesPreserveOriginalByDefault_AndRequiresDoubleConfirmationForOverwrite(OperationKind kind)
    {
        var src = Path.Combine(Path.GetTempPath(), "uc-safety-src.dat");
        var dst = Path.Combine(Path.GetTempPath(), "uc-safety-dst.dat");
        var preview = CreatePreview(kind, src);
        var parameters = new Dictionary<string, string> { ["mode"] = "strict" };

        var defaultToken = OperationConsentToken.Create(kind, parameters, preview);
        Assert.True(defaultToken.PreserveOriginal);
        Assert.False(defaultToken.AllowInPlaceOverwrite);
        Assert.False(defaultToken.ConfirmDestructiveInPlaceOverwrite);

        // Distinct output path succeeds when PreserveOriginal == true.
        MutatingOperationGuard.Enforce(kind, defaultToken, src, dst);

        // Attempting to overwrite source path in-place while PreserveOriginal == true throws.
        Assert.Throws<SafetyConsentRequiredException>(
            () => MutatingOperationGuard.Enforce(kind, defaultToken, src, src));

        // Setting PreserveOriginal = false without BOTH confirmation flags throws.
        var singleConfirmA = defaultToken with
        {
            PreserveOriginal = false,
            AllowInPlaceOverwrite = true,
            ConfirmDestructiveInPlaceOverwrite = false,
        };
        var singleConfirmB = defaultToken with
        {
            PreserveOriginal = false,
            AllowInPlaceOverwrite = false,
            ConfirmDestructiveInPlaceOverwrite = true,
        };
        Assert.Throws<SafetyConsentRequiredException>(() => MutatingOperationGuard.Enforce(kind, singleConfirmA, src, src));
        Assert.Throws<SafetyConsentRequiredException>(() => MutatingOperationGuard.Enforce(kind, singleConfirmB, src, src));

        // Explicit double-confirmation permits intentional override.
        var doubleConfirmed = defaultToken with
        {
            PreserveOriginal = false,
            AllowInPlaceOverwrite = true,
            ConfirmDestructiveInPlaceOverwrite = true,
        };
        MutatingOperationGuard.Enforce(kind, doubleConfirmed, src, src);
    }

    [Fact]
    public void XxHash64AndBitExactVerifier_MatchReferenceVectorsAndDetectCorruption()
    {
        // Official Cyan4973 xxHash64 test vectors (seed 0)
        Assert.Equal(0xEF46DB3751D8E999UL, XxHash64Checksum.HashToUInt64(ReadOnlySpan<byte>.Empty, 0));
        Assert.Equal(0xD24EC4F1A98C6E5BUL, XxHash64Checksum.HashToUInt64("a"u8, 0));
        Assert.Equal(0x44BC2CF5AD770999UL, XxHash64Checksum.HashToUInt64("abc"u8, 0));

        // Multi-stripe (> 32 bytes) deterministic check
        var payload = Encoding.ASCII.GetBytes("UltraCompress bit-exact verification payload across multiple 32-byte stripes!");
        var copy = payload.ToArray();

        var ok = BitExactVerifier.VerifyOrThrow(payload, copy);
        Assert.True(ok.Passed);
        Assert.Equal(64, ok.OriginalSha256.Length);
        Assert.Equal(16, ok.OriginalXxHash64Hex.Length);
        Assert.Equal(ok.OriginalSha256, ok.RestoredSha256);
        Assert.Equal(ok.OriginalXxHash64, ok.RestoredXxHash64);

        // Single bit flip caught
        copy[copy.Length / 2] ^= 0x01;
        var failed = BitExactVerifier.Verify(payload, copy);
        Assert.False(failed.Passed);
        Assert.Throws<BitExactVerificationException>(() => BitExactVerifier.VerifyOrThrow(payload, copy));
    }

    private static OperationImpactPreview CreatePreview(OperationKind kind, string targetPath) =>
        new()
        {
            OperationKind = kind,
            Summary = $"Preview for {kind}",
            Items =
            [
                new ImpactPreviewItem(
                    TargetPath: targetPath,
                    TransformId: kind.ToString().ToLowerInvariant(),
                    Description: "Non-reversible transformation preview item",
                    OriginalBytes: 100_000,
                    EstimatedOutputBytes: 42_000,
                    IsReversible: false),
            ],
        };
}
