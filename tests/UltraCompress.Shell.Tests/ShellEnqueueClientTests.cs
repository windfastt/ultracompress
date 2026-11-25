using System.Text.Json;
using UltraCompress.Core;
using UltraCompress.Shell;

namespace UltraCompress.Shell.Tests;

public sealed class ShellEnqueueClientTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ShellEnqueueClient _client;

    public ShellEnqueueClientTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "uc-shell-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _client = new ShellEnqueueClient(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnqueueAsync_AppendsJsonLineToInbox()
    {
        var requestedAt = new DateTimeOffset(2025, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var request = new ShellEnqueueRequest(
            new[] { @"C:\data\report.pdf", @"C:\data\logs" },
            CompressionProfile.Archive,
            requestedAt);

        await _client.EnqueueAsync(request);

        var inbox = Path.Combine(_tempDir, ShellConstants.DefaultQueueFileName);
        var text = await File.ReadAllTextAsync(inbox);
        Assert.EndsWith(Environment.NewLine, text);

        var line = text.TrimEnd();
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("paths").ValueKind);
        Assert.Equal(2, doc.RootElement.GetProperty("paths").GetArrayLength());
        Assert.Equal("Archive", doc.RootElement.GetProperty("profile").GetString());
    }

    [Fact]
    public async Task EnqueueAsync_ThrowsWhenPathsEmpty()
    {
        var request = new ShellEnqueueRequest(
            Array.Empty<string>(),
            CompressionProfile.Balanced,
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(() => _client.EnqueueAsync(request));
    }
}