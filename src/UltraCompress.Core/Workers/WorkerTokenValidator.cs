namespace UltraCompress.Core.Workers;

public static class WorkerTokenValidator
{
    public static string LoadTokenFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    public static bool TokensMatch(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        if (expected.Length != provided.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            diff |= expected[i] ^ provided[i];
        }

        return diff == 0;
    }
}