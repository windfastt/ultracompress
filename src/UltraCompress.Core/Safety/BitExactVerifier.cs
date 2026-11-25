using System.Security.Cryptography;

namespace UltraCompress.Core.Safety;

/// <summary>
/// Dual-checksum (SHA-256 + XXH64) and byte-exact verification result for lossless compression pipelines.
/// </summary>
public sealed record BitExactVerificationResult(
    bool Passed,
    long OriginalBytes,
    long RestoredBytes,
    string OriginalSha256,
    string RestoredSha256,
    ulong OriginalXxHash64,
    ulong RestoredXxHash64,
    string Message)
{
    public string OriginalXxHash64Hex => OriginalXxHash64.ToString("x16");

    public string RestoredXxHash64Hex => RestoredXxHash64.ToString("x16");
}

/// <summary>
/// Mandatory bit-exact verifier computing both SHA-256 and XXH64 plus span equality.
/// </summary>
public static class BitExactVerifier
{
    public static BitExactVerificationResult Verify(ReadOnlySpan<byte> original, ReadOnlySpan<byte> restored)
    {
        var origXxh = XxHash64Checksum.HashToUInt64(original);
        var restXxh = XxHash64Checksum.HashToUInt64(restored);
        var origSha = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        var restSha = Convert.ToHexString(SHA256.HashData(restored)).ToLowerInvariant();

        if (original.Length != restored.Length)
        {
            return new BitExactVerificationResult(
                Passed: false,
                OriginalBytes: original.Length,
                RestoredBytes: restored.Length,
                OriginalSha256: origSha,
                RestoredSha256: restSha,
                OriginalXxHash64: origXxh,
                RestoredXxHash64: restXxh,
                Message: $"Bit-exact verification failed: restored length {restored.Length} != original length {original.Length}.");
        }

        if (origXxh != restXxh)
        {
            return new BitExactVerificationResult(
                Passed: false,
                OriginalBytes: original.Length,
                RestoredBytes: restored.Length,
                OriginalSha256: origSha,
                RestoredSha256: restSha,
                OriginalXxHash64: origXxh,
                RestoredXxHash64: restXxh,
                Message: $"Bit-exact verification failed: XXH64 mismatch ({restXxh:x16} != {origXxh:x16}).");
        }

        if (!string.Equals(origSha, restSha, StringComparison.Ordinal))
        {
            return new BitExactVerificationResult(
                Passed: false,
                OriginalBytes: original.Length,
                RestoredBytes: restored.Length,
                OriginalSha256: origSha,
                RestoredSha256: restSha,
                OriginalXxHash64: origXxh,
                RestoredXxHash64: restXxh,
                Message: $"Bit-exact verification failed: SHA-256 mismatch ({restSha} != {origSha}).");
        }

        if (!original.SequenceEqual(restored))
        {
            return new BitExactVerificationResult(
                Passed: false,
                OriginalBytes: original.Length,
                RestoredBytes: restored.Length,
                OriginalSha256: origSha,
                RestoredSha256: restSha,
                OriginalXxHash64: origXxh,
                RestoredXxHash64: restXxh,
                Message: "Bit-exact verification failed: decompressed bytes differ from original.");
        }

        return new BitExactVerificationResult(
            Passed: true,
            OriginalBytes: original.Length,
            RestoredBytes: restored.Length,
            OriginalSha256: origSha,
            RestoredSha256: restSha,
            OriginalXxHash64: origXxh,
            RestoredXxHash64: restXxh,
            Message: $"Bit-exact roundtrip verified (SHA-256={origSha[..12]}…, XXH64={origXxh:x16}).");
    }

    public static BitExactVerificationResult VerifyOrThrow(ReadOnlySpan<byte> original, ReadOnlySpan<byte> restored)
    {
        var result = Verify(original, restored);
        if (!result.Passed)
        {
            throw new BitExactVerificationException(result.Message, result);
        }

        return result;
    }
}
