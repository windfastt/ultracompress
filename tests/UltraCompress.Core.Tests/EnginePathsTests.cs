using UltraCompress.Core;

namespace UltraCompress.Core.Tests;

public sealed class EnginePathsTests
{
    [Fact]
    public void GetDefaultWorkerTokenPath_honors_env_override()
    {
        var custom = Path.Combine(Path.GetTempPath(), $"uc-token-{Guid.NewGuid():N}.token");
        var prior = Environment.GetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE");
        try
        {
            Environment.SetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE", custom);
            var resolved = EnginePaths.GetDefaultWorkerTokenPath();
            Assert.Equal(Path.GetFullPath(custom), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE", prior);
        }
    }

    [Fact]
    public void GetDefaultWorkerTokenPath_defaults_under_program_data_root()
    {
        var priorFile = Environment.GetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE");
        var priorRoot = Environment.GetEnvironmentVariable("ULTRACOMPRESS_PROGRAM_DATA");
        try
        {
            Environment.SetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE", null);
            if (!OperatingSystem.IsWindows())
            {
                var root = Path.Combine(Path.GetTempPath(), $"uc-pd-{Guid.NewGuid():N}");
                Environment.SetEnvironmentVariable("ULTRACOMPRESS_PROGRAM_DATA", root);
                var expected = Path.Combine(root, EnginePaths.ProductFolderName, EnginePaths.WorkerTokenFileName);
                Assert.Equal(Path.GetFullPath(expected), EnginePaths.GetDefaultWorkerTokenPath());
            }
            else
            {
                var expected = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    EnginePaths.ProductFolderName,
                    EnginePaths.WorkerTokenFileName);
                Assert.Equal(Path.GetFullPath(expected), EnginePaths.GetDefaultWorkerTokenPath());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ULTRACOMPRESS_WORKER_TOKEN_FILE", priorFile);
            Environment.SetEnvironmentVariable("ULTRACOMPRESS_PROGRAM_DATA", priorRoot);
        }
    }

    [Fact]
    public void GetProgramDataRoot_uses_ultracompress_folder_name()
    {
        var root = EnginePaths.GetProgramDataRoot();
        Assert.EndsWith(EnginePaths.ProductFolderName, root, StringComparison.Ordinal);
    }
}