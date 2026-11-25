namespace UltraCompress.Core.Search;

public sealed record ArmTrialResult(
    string ArmId,
    long OriginalBytes,
    long CompressedBytes,
    double WallSeconds,
    bool Verified,
    PipelineRecipe Recipe)
{
    public long BytesSaved => Math.Max(0, OriginalBytes - CompressedBytes);

    /// <summary>Bandit reward: bytes saved per second (maximize).</summary>
    public double Reward => BytesSaved / Math.Max(1.0, WallSeconds);
}