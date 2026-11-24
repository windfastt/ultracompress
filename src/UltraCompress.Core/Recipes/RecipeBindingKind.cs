namespace UltraCompress.Core.Recipes;

/// <summary>
/// How a stored recipe is keyed for lookup (blueprint §4.2 manual patch + cache).
/// </summary>
public enum RecipeBindingKind
{
    /// <summary>SHA-256 hex of file contents; optional size/mtime for cache invalidation.</summary>
    ContentHash,

    /// <summary>Normalized absolute path (pinned manual override).</summary>
    Path,

    /// <summary>Simple glob on path (e.g. <c>*.pdf</c>, <c>C:\logs\*</c>).</summary>
    PathGlob,

    /// <summary>File extension including dot, lowercased (e.g. <c>.pdf</c>).</summary>
    Extension,
}