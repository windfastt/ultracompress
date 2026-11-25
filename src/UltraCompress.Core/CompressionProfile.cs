using System.Text.Json.Serialization;

namespace UltraCompress.Core;

/// <summary>
/// Five strictly bit-exact lossless compression behavior tiers, governed by <see cref="ProfileContract"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompressionProfile
{
    Instant = 0,
    Balanced = 1,
    Archive = 2,
    Tiny = 3,
    Laboratory = 4,
}

/// <summary>
/// Parses canonical <see cref="CompressionProfile"/> names as well as legacy tier aliases.
/// </summary>
public static class CompressionProfileParser
{
    public static bool TryParse(string? value, out CompressionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            profile = CompressionProfile.Balanced;
            return false;
        }

        var trimmed = value.Trim();
        if (Enum.TryParse(trimmed, ignoreCase: true, out profile) && Enum.IsDefined(profile))
        {
            return true;
        }

        if (trimmed.Equals("Small", StringComparison.OrdinalIgnoreCase))
        {
            profile = CompressionProfile.Archive;
            return true;
        }

        if (trimmed.Equals("Experimental", StringComparison.OrdinalIgnoreCase))
        {
            profile = CompressionProfile.Laboratory;
            return true;
        }

        profile = CompressionProfile.Balanced;
        return false;
    }

    public static CompressionProfile ParseOrDefault(string? value, CompressionProfile fallback = CompressionProfile.Balanced) =>
        TryParse(value, out var parsed) ? parsed : fallback;
}
