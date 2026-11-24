namespace UltraCompress.Core.Recipes;

public static class RecipeStorePaths
{
    /// <summary>Default install location from blueprint §6.</summary>
    public static string DefaultRecipeDatabasePath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "UltraCompress", "recipes.db");
    }
}