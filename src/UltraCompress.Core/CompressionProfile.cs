using System.Text.Json.Serialization;

namespace UltraCompress.Core;

/// <summary>
/// User-facing compression behavior tiers (see docs/user/USER.md).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompressionProfile
{
    Balanced,
    Small,
    Tiny,
    Laboratory,
    Experimental,
}