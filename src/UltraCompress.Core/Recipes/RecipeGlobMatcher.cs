namespace UltraCompress.Core.Recipes;

internal static class RecipeGlobMatcher
{
    /// <summary>Case-insensitive glob with <c>*</c> and <c>?</c> (Windows paths).</summary>
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> path)
    {
        return IsMatchRecursive(pattern, path);
    }

    private static bool IsMatchRecursive(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        while (true)
        {
            if (pattern.IsEmpty)
            {
                return text.IsEmpty;
            }

            if (pattern[0] == '*')
            {
                pattern = pattern[1..];
                if (pattern.IsEmpty)
                {
                    return true;
                }

                for (var i = 0; i <= text.Length; i++)
                {
                    if (IsMatchRecursive(pattern, text[i..]))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (text.IsEmpty)
            {
                return false;
            }

            if (pattern[0] == '?')
            {
                pattern = pattern[1..];
                text = text[1..];
                continue;
            }

            if (!CharsEqual(pattern[0], text[0]))
            {
                return false;
            }

            pattern = pattern[1..];
            text = text[1..];
        }
    }

    private static bool CharsEqual(char a, char b) =>
        char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}