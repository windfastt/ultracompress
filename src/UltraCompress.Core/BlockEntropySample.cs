namespace UltraCompress.Core;

/// <summary>
/// Shannon entropy for one fixed-size slice of a file.
/// </summary>
public sealed record BlockEntropySample
{
    public required long Offset { get; init; }

    public required int Length { get; init; }

    public required double EntropyBitsPerByte { get; init; }
}