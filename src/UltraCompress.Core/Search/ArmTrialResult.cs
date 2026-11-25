namespace UltraCompress.Core.Search;

public sealed record ArmTrialResult(
    string ArmId,
    long OriginalBytes,
    long CompressedBytes,
    double WallSeconds,
    bool Verified,
    PipelineRecipe Recipe,
    int RungIndex = 0)
{
    public long BytesSaved => Math.Max(0, OriginalBytes - CompressedBytes);

    public double Ratio => OriginalBytes <= 0 ? 1.0 : (double)CompressedBytes / OriginalBytes;

    /// <summary>Bandit reward: bytes saved per second (maximize).</summary>
    public double Reward => BytesSaved / Math.Max(1.0, WallSeconds);
}
