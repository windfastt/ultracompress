namespace UltraCompress.Core.Recipes;

/// <summary>
/// Persistent per-file and per-type recipe overrides (<c>recipes.db</c>).
/// </summary>
public interface IRecipeStore : IDisposable
{
    /// <summary>Creates the database file and applies migrations if needed.</summary>
    void EnsureInitialized();

    /// <summary>Stores or replaces a user-pinned recipe binding.</summary>
    void PinRecipe(
        RecipeBindingKind kind,
        string bindingKey,
        PipelineRecipe recipe,
        string? notes = null);

    /// <summary>
    /// Stores a tournament/search winner keyed by content hash (not pinned; invalidated when size/mtime change).
    /// </summary>
    void SaveWinningCache(FileFingerprint fingerprint, PipelineRecipe recipe);

    /// <summary>
    /// Resolves a recipe for a file using blueprint priority: content hash, path, longest path glob, extension.
    /// </summary>
    bool TryResolveRecipe(FileFingerprint fingerprint, out PipelineRecipe? recipe);

    bool RemoveBinding(RecipeBindingKind kind, string bindingKey);

    IReadOnlyList<RecipeBindingRecord> ListBindings(RecipeBindingKind? kindFilter = null);
}