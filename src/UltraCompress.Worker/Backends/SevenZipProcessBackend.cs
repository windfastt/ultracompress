using UltraCompress.Core;

namespace UltraCompress.Worker.Backends;

/// <summary>
/// 7z container backend via LGPL <c>7za</c> subprocess (isolated from the service host).
/// </summary>
public sealed class SevenZipProcessBackend : ICompressionBackend
{
    public const string DefaultEntryName = "payload.bin";

    private readonly SevenZipProcessRunner _runner;

    public SevenZipProcessBackend(string? sevenZipExecutablePath = null)
    {
        _runner = new SevenZipProcessRunner(sevenZipExecutablePath);
    }

    public string CodecName => "7z";

    public Task<byte[]> CompressAsync(
        ReadOnlyMemory<byte> input,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        return _runner.CreateArchiveAsync(input, DefaultEntryName, step, cancellationToken);
    }

    public Task<byte[]> DecompressAsync(
        ReadOnlyMemory<byte> compressed,
        CompressStep step,
        CancellationToken cancellationToken = default)
    {
        return _runner.ExtractFirstEntryAsync(compressed, DefaultEntryName, cancellationToken);
    }
}