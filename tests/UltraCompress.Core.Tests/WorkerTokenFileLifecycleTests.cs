using UltraCompress.Core;
using UltraCompress.Core.Workers;

namespace UltraCompress.Core.Tests;

public sealed class WorkerTokenFileLifecycleTests
{
    [Fact]
    public void Write_and_read_roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"uc-wt-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "worker.token");
        WorkerTokenFileLifecycle.WriteTokenFile(path, "lab-secret-42");
        Assert.Equal("lab-secret-42", WorkerTokenFileLifecycle.ReadTokenOrEmpty(path));
        Assert.True(WorkerTokenFileLifecycle.TokenFileExists(path));
    }

    [Fact]
    public void Write_refuses_overwrite_by_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uc-wt-{Guid.NewGuid():N}", "worker.token");
        WorkerTokenFileLifecycle.WriteTokenFile(path, "one");
        Assert.Throws<InvalidOperationException>(() => WorkerTokenFileLifecycle.WriteTokenFile(path, "two"));
        Assert.Equal("one", WorkerTokenFileLifecycle.ReadTokenOrEmpty(path));
    }

    [Fact]
    public void Write_with_overwrite_replaces_token()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uc-wt-{Guid.NewGuid():N}", "worker.token");
        WorkerTokenFileLifecycle.WriteTokenFile(path, "one");
        WorkerTokenFileLifecycle.WriteTokenFile(path, "two", overwrite: true);
        Assert.Equal("two", WorkerTokenFileLifecycle.ReadTokenOrEmpty(path));
    }

    [Fact]
    public void ResolveReadPath_uses_default_when_null()
    {
        var prior = Environment.GetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE");
        try
        {
            Environment.SetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE", null);
            Assert.Equal(EnginePaths.GetDefaultWorkerTokenPath(), WorkerTokenFileLifecycle.ResolveReadPath(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE", prior);
        }
    }

    [Fact]
    public void Validator_skips_comment_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uc-wt-{Guid.NewGuid():N}", "worker.token");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# installer generated\n\ntoken-value\n");
        Assert.Equal("token-value", WorkerTokenFileLifecycle.ReadTokenOrEmpty(path));
    }
}