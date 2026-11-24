using UltraCompress.Core.Recipes;

namespace UltraCompress.Core.Tests;

public sealed class RecipeGlobMatcherTests
{
    [Theory]
    [InlineData("*.pdf", "report.pdf", true)]
    [InlineData("*.PDF", "report.pdf", true)]
    [InlineData(@"C:\data\*", @"C:\data\file.bin", true)]
    [InlineData(@"C:\data\*", @"C:\other\file.bin", false)]
    [InlineData("foo?", "foot", true)]
    [InlineData("foo?", "foots", false)]
    public void IsMatch_WorksForWildcardPatterns(string pattern, string path, bool expected)
    {
        Assert.Equal(
            expected,
            RecipeGlobMatcher.IsMatch(pattern.AsSpan(), path.AsSpan()));
    }
}