namespace UltraCompress.Core.Recipes;

internal static class RecipePathNormalization
{
    public static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/');

    public static string NormalizeExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
    }
}