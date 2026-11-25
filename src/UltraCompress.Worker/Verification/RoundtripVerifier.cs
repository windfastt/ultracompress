using UltraCompress.Core;
using UltraCompress.Core.Safety;

namespace UltraCompress.Worker.Verification;

/// <summary>
/// Verifies lossless round-trip using dual SHA-256 and XXH64 checksums plus byte-exact sequence comparison.
/// </summary>
public sealed class RoundtripVerifier
{
    public VerificationResult Verify(byte[] original, byte[] roundtripped, VerifyOptions options)
    {
        if (!options.DecompressRoundtrip)
        {
            return VerificationResult.Skipped("Round-trip verification disabled by recipe.");
        }

        var bitExact = BitExactVerifier.Verify(original, roundtripped);
        if (!bitExact.Passed)
        {
            return VerificationResult.Failed(bitExact.Message);
        }

        return options.HashOriginal
            ? VerificationResult.Success(bitExact.OriginalSha256, bitExact.OriginalXxHash64Hex)
            : VerificationResult.Success(null, bitExact.OriginalXxHash64Hex);
    }

    public static VerificationResult FromBitExact(BitExactVerificationResult bitExact) =>
        bitExact.Passed
            ? VerificationResult.Success(bitExact.OriginalSha256, bitExact.OriginalXxHash64Hex)
            : VerificationResult.Failed(bitExact.Message);
}

public sealed record VerificationResult(
    bool Passed,
    string? OriginalSha256,
    string Message,
    string? OriginalXxHash64 = null)
{
    public static VerificationResult Success(string? sha256, string? xxHash64 = null) =>
        new(true, sha256, "Bit-exact round-trip verification passed.", xxHash64);

    public static VerificationResult Failed(string message) =>
        new(false, null, message, null);

    public static VerificationResult Skipped(string message) =>
        new(true, null, message, null);
}
