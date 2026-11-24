namespace UltraCompress.Core.Recipes;

/// <summary>Row metadata for a persisted recipe binding.</summary>
public sealed record RecipeBindingRecord
{
    public required long Id { get; init; }

    public required RecipeBindingKind Kind { get; init; }

    public required string BindingKey { get; init; }

    public long? SizeBytes { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; init; }

    public required string RecipeId { get; init; }

    public required bool IsPinned { get; init; }

    public string? Notes { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }
}