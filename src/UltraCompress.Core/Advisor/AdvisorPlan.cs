namespace UltraCompress.Core.Advisor;

/// <summary>Advisor output: primary pipeline plus optional tournament alternates.</summary>
public sealed class AdvisorPlan
{
    public required PipelineRecipe PrimaryRecipe { get; init; }

    /// <summary>Primary codec suggestion (mirrors the last compress step on <see cref="PrimaryRecipe"/>).</summary>
    public required CodecProfileSuggestion PrimaryCodec { get; init; }

    public IReadOnlyList<CodecProfileSuggestion> AlternateCodecs { get; init; } = Array.Empty<CodecProfileSuggestion>();

    public IReadOnlyList<PipelineRecipe> TournamentCandidates { get; init; } = Array.Empty<PipelineRecipe>();

    public IReadOnlyList<AdvisorDecision> Decisions { get; init; } = Array.Empty<AdvisorDecision>();
}