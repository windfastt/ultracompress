using UltraCompress.Core.Errors;

namespace UltraCompress.Core.Archive;

public static class UcxPathSecurity
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    /// Normalizes a relative archive path to forward-slash form and validates that it contains
    /// no traversal segments, rooted prefixes, drive letters, or control characters.
    /// </summary>
    public static string NormalizeAndValidateRelativePath(string relativePath, string? archivePath = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw UltraCompressErrors.PathTraversalDetected(
                archivePath,
                relativePath ?? string.Empty,
                "Archive entry path cannot be empty or whitespace.");
        }

        if (relativePath.IndexOf('\0') >= 0)
        {
            throw UltraCompressErrors.PathTraversalDetected(
                archivePath,
                relativePath,
                "Archive entry path contains a NUL byte.");
        }

        if (relativePath[0] is '/' or '\\')
        {
            throw UltraCompressErrors.PathTraversalDetected(
                archivePath,
                relativePath,
                "Archive entry path must be relative, not rooted.");
        }

        if (relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw UltraCompressErrors.PathTraversalDetected(
                archivePath,
                relativePath,
                "Archive entry path cannot contain volume/drive or alternate-data-stream colons.");
        }

        var rawSegments = relativePath.Split(PathSeparators, StringSplitOptions.None);
        var cleanSegments = new List<string>(rawSegments.Length);

        foreach (var seg in rawSegments)
        {
            if (string.IsNullOrEmpty(seg) || seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                throw UltraCompressErrors.PathTraversalDetected(
                    archivePath,
                    relativePath,
                    "Parent directory traversal ('..') is forbidden in archive entries.");
            }

            cleanSegments.Add(seg);
        }

        if (cleanSegments.Count == 0)
        {
            throw UltraCompressErrors.PathTraversalDetected(
                archivePath,
                relativePath,
                "Archive entry path resolved to empty root.");
        }

        return string.Join('/', cleanSegments);
    }

    /// <summary>
    /// Resolves an archive entry against <paramref name="destinationDirectory"/> with strict zip-slip
    /// and symlink/reparse-point traversal protection.
    /// </summary>
    public static string ValidateAndResolveExtractPath(
        string? archivePath,
        string entryRelativePath,
        string destinationDirectory)
    {
        var normalizedRel = NormalizeAndValidateRelativePath(entryRelativePath, archivePath);
        var fullRoot = Path.GetFullPath(destinationDirectory);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        var osRelative = normalizedRel.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(fullRoot, osRelative));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!combined.StartsWith(rootWithSep, comparison) || string.Equals(combined, fullRoot, comparison))
        {
            throw UltraCompressErrors.PathTraversalDetected(
                archivePath,
                entryRelativePath,
                $"Resolved target '{combined}' escapes destination directory '{fullRoot}'.");
        }

        // Guard against pre-existing symlink / junction escape inside destination tree.
        var segments = normalizedRel.Split('/');
        var probe = fullRoot;
        for (var i = 0; i < segments.Length; i++)
        {
            probe = Path.Combine(probe, segments[i]);
            if (Directory.Exists(probe))
            {
                var dirInfo = new DirectoryInfo(probe);
                if (dirInfo.LinkTarget is not null || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw UltraCompressErrors.PathTraversalDetected(
                        archivePath,
                        entryRelativePath,
                        $"Intermediate directory '{probe}' is a symbolic link or reparse point.");
                }
            }
            else if (File.Exists(probe))
            {
                var fileInfo = new FileInfo(probe);
                if (fileInfo.LinkTarget is not null || (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw UltraCompressErrors.PathTraversalDetected(
                        archivePath,
                        entryRelativePath,
                        $"Target path '{probe}' is a symbolic link or reparse point.");
                }
            }
        }

        return combined;
    }

    /// <summary>
    /// Matches normalized forward-slash archive paths against exact paths, subfolder prefixes,
    /// or glob patterns (<c>*</c>, <c>**</c>, <c>?</c>).
    /// </summary>
    public static bool MatchesSelectPatterns(string normalizedEntryPath, IReadOnlyList<string>? selectPatterns)
    {
        if (selectPatterns is null || selectPatterns.Count == 0)
        {
            return true;
        }

        var path = normalizedEntryPath.Trim('/');
        foreach (var rawPattern in selectPatterns)
        {
            if (string.IsNullOrWhiteSpace(rawPattern))
            {
                continue;
            }

            var pattern = rawPattern.Trim().Replace('\\', '/');
            if (pattern.StartsWith("./", StringComparison.Ordinal))
            {
                pattern = pattern[2..];
            }

            if (MatchesSinglePattern(path, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSinglePattern(string path, string pattern)
    {
        var trimmedPattern = pattern.Trim('/');
        if (string.IsNullOrEmpty(trimmedPattern))
        {
            return true;
        }

        bool hasWildcard = trimmedPattern.IndexOfAny(['*', '?']) >= 0;
        if (!hasWildcard)
        {
            // Exact file/dir match OR subfolder prefix match (e.g., "src/Core" matches "src/Core/Foo.cs").
            if (string.Equals(path, trimmedPattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (path.StartsWith(trimmedPattern + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        // If pattern has no slash (e.g. "*.json"), match either full relative path or leaf file name!
        if (!trimmedPattern.Contains('/', StringComparison.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (MatchSegmentGlob(trimmedPattern.AsSpan(), fileName.AsSpan()))
            {
                return true;
            }
        }

        var patternSegments = trimmedPattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return MatchSegmentsRecursive(patternSegments, 0, pathSegments, 0);
    }

    private static bool MatchSegmentsRecursive(
        string[] patternSegments,
        int pIdx,
        string[] pathSegments,
        int sIdx)
    {
        while (pIdx < patternSegments.Length)
        {
            var pat = patternSegments[pIdx];
            if (pat == "**")
            {
                while (pIdx + 1 < patternSegments.Length && patternSegments[pIdx + 1] == "**")
                {
                    pIdx++;
                }

                if (pIdx == patternSegments.Length - 1)
                {
                    return true;
                }

                for (var skip = sIdx; skip <= pathSegments.Length; skip++)
                {
                    if (MatchSegmentsRecursive(patternSegments, pIdx + 1, pathSegments, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (sIdx >= pathSegments.Length)
            {
                return false;
            }

            if (!MatchSegmentGlob(pat.AsSpan(), pathSegments[sIdx].AsSpan()))
            {
                return false;
            }

            pIdx++;
            sIdx++;
        }

        return sIdx == pathSegments.Length;
    }

    private static bool MatchSegmentGlob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
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
                    if (MatchSegmentGlob(pattern, text[i..]))
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

            if (pattern[0] == '?' || char.ToUpperInvariant(pattern[0]) == char.ToUpperInvariant(text[0]))
            {
                pattern = pattern[1..];
                text = text[1..];
                continue;
            }

            return false;
        }
    }
}
