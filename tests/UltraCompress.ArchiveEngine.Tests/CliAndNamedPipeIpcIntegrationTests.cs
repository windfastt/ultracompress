using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using UltraCompress.Core;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Errors;
using UltraCompress.Core.Ipc;
using UltraCompress.Core.Jobs;
using UltraCompress.Engine;
using UltraCompress.Engine.Ipc;
using UltraCompress.Engine.Jobs;
using UltraCompress.Worker;

namespace UltraCompress.ArchiveEngine.Tests;

public sealed class CliAndNamedPipeIpcIntegrationTests
{
    [Fact]
    public async Task Cli_PackListVerifyExtractSelectAndHistory_RoundtripEndToEnd()
    {
        using var ws = new TempTestWorkspace();
        var dbPath = ws.SubPath("cli-db", "jobs.db");
        ws.WriteTextFile("app/src/Program.cs", string.Concat(Enumerable.Repeat("Console.WriteLine(\"Hello UCX\");\n", 200)));
        ws.WriteTextFile("app/src/Helper.cs", string.Concat(Enumerable.Repeat("internal static class Helper {}\n", 200)));
        ws.WriteTextFile("app/config/settings.json", "{\"mode\":\"production\"}\n");
        ws.WriteBinaryFile("app/media/icon.jpg", TempTestWorkspace.CreateSyntheticJpegBytes(4096));

        var archivePath = ws.SubPath("out", "app.ucx");

        // 1. CLI pack
        var (packCode, packOut, packErr) = await WorkerCli.InvokeCapturedAsync(
            "pack", "-i", ws.SubPath("app"), "-o", archivePath, "-p", "Balanced", "--db", dbPath);
        Assert.Equal(0, packCode);
        Assert.Equal(string.Empty, packErr);
        Assert.Contains("Archive:", packOut, StringComparison.Ordinal);

        // 2. CLI list --select "**/*.cs" --json
        var (listCode, listOut, _) = await WorkerCli.InvokeCapturedAsync(
            "list", "-a", archivePath, "--select", "**/*.cs", "--json");
        Assert.Equal(0, listCode);
        using (var doc = JsonDocument.Parse(listOut))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("entries").GetArrayLength());
        }

        // 3. CLI verify
        var (verifyCode, verifyOut, _) = await WorkerCli.InvokeCapturedAsync("verify", "-a", archivePath);
        Assert.Equal(0, verifyCode);
        Assert.Contains("Verify OK:", verifyOut, StringComparison.Ordinal);

        // 4. CLI extract --select "**/*.cs"
        var extractDir = ws.SubPath("cli-extracted");
        var (extCode, _, extErr) = await WorkerCli.InvokeCapturedAsync(
            "extract", "-a", archivePath, "-o", extractDir, "--select", "**/*.cs", "--db", dbPath);
        Assert.Equal(0, extCode);
        Assert.Equal(string.Empty, extErr);
        Assert.True(File.Exists(Path.Combine(extractDir, "src", "Program.cs")));
        Assert.True(File.Exists(Path.Combine(extractDir, "src", "Helper.cs")));
        Assert.False(File.Exists(Path.Combine(extractDir, "config", "settings.json")));

        // 5. CLI history --json
        var (histCode, histOut, _) = await WorkerCli.InvokeCapturedAsync("history", "--db", dbPath, "--json");
        Assert.Equal(0, histCode);
        using (var histDoc = JsonDocument.Parse(histOut))
        {
            Assert.Equal(2, histDoc.RootElement.GetProperty("entries").GetArrayLength());
            Assert.Equal(1, histDoc.RootElement.GetProperty("summary").GetProperty("totalPackOperations").GetInt32());
            Assert.Equal(1, histDoc.RootElement.GetProperty("summary").GetProperty("totalExtractOperations").GetInt32());
        }

        // 6. CLI error taxonomy & exit code on missing input and destination conflict
        var (missingCode, _, missingErr) = await WorkerCli.InvokeCapturedAsync(
            "pack", "-i", ws.SubPath("does-not-exist.bin"));
        Assert.Equal(3, missingCode);
        Assert.Contains("UC-ERR-IO-001", missingErr, StringComparison.Ordinal);
        Assert.Contains("Remediation:", missingErr, StringComparison.Ordinal);

        var (conflictCode, _, conflictErr) = await WorkerCli.InvokeCapturedAsync(
            "pack", "-i", ws.SubPath("app"), "-o", archivePath, "--conflict", "Fail");
        Assert.Equal(6, conflictCode);
        Assert.Contains("UC-ERR-DEST-001", conflictErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedPipeIpc_PackListVerifyExtractHistoryAndRecovery_WorkOverLivePipe()
    {
        using var ws = new TempTestWorkspace();
        var pipeName = $"UltraCompress.Test.{Guid.NewGuid():N}";
        var dbPath = ws.SubPath("ipc", "jobs.db");

        ws.WriteTextFile("pkg/code/Service.cs", string.Concat(Enumerable.Repeat("public sealed class Service {}\n", 250)));
        ws.WriteTextFile("pkg/data/items.json", string.Concat(Enumerable.Repeat("{\"id\":1,\"ok\":true}\n", 250)));
        ws.WriteTextFile("pkg/data/items-dup.json", string.Concat(Enumerable.Repeat("{\"id\":1,\"ok\":true}\n", 250)));

        var state = new EngineRuntimeState(new JobQueueService());
        await using var archiveEngine = new ResilientArchiveEngine(new SqliteJobJournal(dbPath));
        archiveEngine.RecoverOnStartup();
        archiveEngine.StartBackgroundWorker();

        var pipeHost = new EnginePipeHostedService(
            NullLogger<EnginePipeHostedService>.Instance,
            state,
            archiveEngine,
            pipeName);

        await pipeHost.StartAsync(CancellationToken.None);
        try
        {
            using var client = new NamedPipeEngineClient(TimeSpan.FromSeconds(5), pipeName);

            Assert.True(await client.PingAsync());

            var archivePath = ws.SubPath("ipc-out", "pkg.ucx");
            var packResp = await client.PackArchiveAsync(
                [ws.SubPath("pkg")],
                outputPath: archivePath,
                profile: CompressionProfile.Archive,
                destinationMode: DestinationMode.CustomDirectory,
                conflictPolicy: DestinationConflictPolicy.Overwrite,
                waitForCompletion: true);

            Assert.NotNull(packResp);
            Assert.True(packResp!.Success);
            Assert.NotNull(packResp.ArchiveJob);
            Assert.Equal(ArchiveJobLifecycleState.Completed, packResp.ArchiveJob!.Status);
            Assert.Equal(1, packResp.ArchiveJob.DuplicateCount);

            // List over IPC
            var listResp = await client.ListArchiveAsync(archivePath, selectPatterns: ["*.json"]);
            Assert.NotNull(listResp);
            Assert.True(listResp!.Success);
            Assert.Equal(2, listResp.ArchiveEntries!.Count);
            Assert.Equal(3, listResp.ArchiveInspection!.Summary.FileCount);

            // Verify over IPC
            var verifyResp = await client.VerifyArchiveAsync(archivePath);
            Assert.NotNull(verifyResp);
            Assert.True(verifyResp!.Success);
            Assert.True(verifyResp.VerifyResult!.IsValid);

            // Selective Extract over IPC
            var extractDest = ws.SubPath("ipc-extract");
            var extractResp = await client.ExtractArchiveAsync(
                archivePath,
                outputDirectory: extractDest,
                selectPatterns: ["code/**"],
                destinationMode: DestinationMode.CustomDirectory,
                conflictPolicy: DestinationConflictPolicy.Overwrite,
                waitForCompletion: true);

            Assert.NotNull(extractResp);
            Assert.True(extractResp!.Success);
            Assert.True(File.Exists(Path.Combine(extractDest, "code", "Service.cs")));
            Assert.False(File.Exists(Path.Combine(extractDest, "data", "items.json")));

            // History over IPC
            var histResp = await client.GetHistoryAsync(limit: 10);
            Assert.NotNull(histResp);
            Assert.True(histResp!.Success);
            Assert.Equal(2, histResp.HistoryEntries!.Count);
            Assert.True(histResp.HistorySummary!.CumulativeSavedBytes > 0);

            // Error propagation over IPC
            var errResp = await client.ListArchiveAsync(ws.SubPath("nonexistent.ucx"));
            Assert.NotNull(errResp);
            Assert.Equal(EngineMessageTypes.Error, errResp!.Type);
            Assert.Equal(UltraCompressErrorCode.InputNotFound, errResp.Error?.Code);
        }
        finally
        {
            await pipeHost.StopAsync(CancellationToken.None);
        }
    }
}
