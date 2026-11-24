namespace UltraCompress.Core.Advisor;

/// <summary>Codec and level hint produced by advisor rules (one compress step).</summary>
public sealed record CodecProfileSuggestion
{
    public required string Codec { get; init; }

    public required int Level { get; init; }

    public int? DictSizeKb { get; init; }

    public string? MatchedRuleId { get; init; }
}