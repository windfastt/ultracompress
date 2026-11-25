namespace UltraCompress.Core.Recipes;

public static class RecipeStorePaths
{
    public const string RecipeDatabaseEnvironmentVariable = "ULTRACOMPRESS_RECIPE_DB";

    /// <summary>Default persistent SQLite recipe database path.</summary>
    public static string DefaultRecipeDatabasePath()
    {
        var fromEnv = Environment.GetEnvironmentVariable(RecipeDatabaseEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv.Trim());
        }

        return Path.Combine(EnginePaths.GetProgramDataRoot(), "recipes.db");
    }
}
