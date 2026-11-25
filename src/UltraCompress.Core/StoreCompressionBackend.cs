namespace UltraCompress.Core;

/// <summary>
/// Zero-expansion lossless pass-through backend (<c>store</c>) selected when data is incompressible
/// (entropy bypass or compressed ratio &gt;= 0.995).
/// </summary>
public sealed class StoreCompressionBackend : ICompressionBackend
{
    public static readonly StoreCompressionBackend Instance = new();

    public string CodecName => "store";

    public Task<byte[]> CompressAsync(
        ReadOnlyMemory<byte> input,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(input.ToArray());
    }

    public Task<byte[]> DecompressAsync(
        ReadOnlyMemory<byte> compressed,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(compressed.ToArray());
    }
}
