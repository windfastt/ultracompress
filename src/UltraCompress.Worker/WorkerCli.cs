using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltraCompress.Core;
using UltraCompress.Core.Archive;
using UltraCompress.Core.Destination;
using UltraCompress.Core.Errors;
using UltraCompress.Core.History;

namespace UltraCompress.Worker;

public static class WorkerCli
{
    private static readonly JsonSerializerOptions CliJsonOptions = CreateJsonOptions();

    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("UltraCompress CLI — content-aware .ucx archive manager & lossless compressor");

        root.AddCommand(BuildSingleCompressCommand());
        root.AddCommand(BuildPackCommand());
        root.AddCommand(BuildListCommand());
        root.AddCommand(BuildExtractCommand());
        root.AddCommand(BuildVerifyCommand());
        root.AddCommand(BuildHistoryCommand());

        return root;
    }

    public static Task<int> InvokeAsync(string[] args, IConsole? console = null)
    {
        var root = BuildRootCommand();
        return root.InvokeAsync(args, console);
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> InvokeCapturedAsync(params string[] args)
    {
        var testConsole = new BufferedConsole();
        int code = await InvokeAsync(args, testConsole).ConfigureAwait(false);
        return (code, testConsole.OutText, testConsole.ErrorText);
    }

    private static Command BuildSingleCompressCommand()
    {
        var inputOption = new Option<FileInfo>(
            name: "--input",
            description: "File to compress")
        { IsRequired = true };
        inputOption.AddAlias("-i");

        var profileOption = new Option<CompressionProfile>(
            name: "--profile",
            getDefaultValue: () => CompressionProfile.Balanced,
            description: "Compression profile (Instant, Balanced, Archive, Tiny, Laboratory)");
        profileOption.AddAlias("-p");

        var outputOption = new Option<FileInfo?>(
            name: "--output",
            description: "Output path (default: <input>.uc.zst / .uc.br / .uc.deflate / .uc.7z by codec)");
        outputOption.AddAlias("-o");

        var compressCommand = new Command("compress", "Compress a single file using the profile default recipe")
        {
            inputOption,
            profileOption,
            outputOption,
        };

        compressCommand.SetHandler(async (InvocationContext ctx) =>
        {
            var input = ctx.ParseResult.GetValueForOption(inputOption)!;
            var profile = ctx.ParseResult.GetValueForOption(profileOption);
            var output = ctx.ParseResult.GetValueForOption(outputOption);

            try
            {
                var runner = new CompressionJobRunner();
                var result = await runner.RunAsync(
                    input.FullName,
                    profile,
                    output?.FullName,
                    ctx.GetCancellationToken()).ConfigureAwait(false);

                ctx.Console.Out.WriteLine($"Input:      {result.Fingerprint.Path}");
                ctx.Console.Out.WriteLine($"Profile:    {profile}");
                ctx.Console.Out.WriteLine($"Recipe:     {result.Recipe.Id}");
                ctx.Console.Out.WriteLine($"Output:     {result.OutputPath}");
                ctx.Console.Out.WriteLine($"Size:       {result.OriginalBytes} -> {result.CompressedBytes} ({result.Ratio:P1})");
                ctx.Console.Out.WriteLine($"Verify:     {result.Verification.Message}");

                ctx.ExitCode = result.Verification.Passed ? 0 : 2;
            }
            catch (Exception ex)
            {
                WriteFormattedError(ctx, ex, input.FullName);
            }
        });

        return compressCommand;
    }

    private static Command BuildPackCommand()
    {
        var inputsOption = new Option<string[]>(
            name: "--input",
            description: "One or more files or folders to pack into a .ucx archive")
        {
            IsRequired = true,
            AllowMultipleArgumentsPerToken = true,
        };
        inputsOption.AddAlias("-i");

        var outputOption = new Option<string?>(
            name: "--output",
            description: "Output .ucx archive path or destination directory");
        outputOption.AddAlias("-o");

        var profileOption = new Option<CompressionProfile>(
            name: "--profile",
            getDefaultValue: () => CompressionProfile.Balanced,
            description: "Lossless compression profile");
        profileOption.AddAlias("-p");

        var destModeOption = new Option<DestinationMode?>(
            name: "--dest-mode",
            description: "Destination resolution mode (SideBySide, CustomDirectory, ArchiveSubfolder)");

        var conflictOption = new Option<DestinationConflictPolicy>(
            name: "--conflict",
            getDefaultValue: () => DestinationConflictPolicy.Fail,
            description: "Destination conflict policy (Fail, Overwrite, RenameNumbered, SkipExisting)");

        var codecOption = new Option<string?>(
            name: "--codec",
            description: "Optional explicit codec override (store, zstd, brotli, deflate, 7z)");

        var preserveRootOption = new Option<bool>(
            name: "--preserve-root",
            getDefaultValue: () => false,
            description: "Include single root folder name as top-level prefix inside archive");

        var dbOption = new Option<string?>(
            name: "--db",
            description: "Optional SQLite history/jobs database path");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Emit structured JSON output");

        var cmd = new Command("pack", "Pack files and/or folders into a content-aware .ucx multi-file archive")
        {
            inputsOption,
            outputOption,
            profileOption,
            destModeOption,
            conflictOption,
            codecOption,
            preserveRootOption,
            dbOption,
            jsonOption,
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var inputs = ctx.ParseResult.GetValueForOption(inputsOption) ?? Array.Empty<string>();
            var output = ctx.ParseResult.GetValueForOption(outputOption);
            var profile = ctx.ParseResult.GetValueForOption(profileOption);
            var explicitMode = ctx.ParseResult.GetValueForOption(destModeOption);
            var conflict = ctx.ParseResult.GetValueForOption(conflictOption);
            var codec = ctx.ParseResult.GetValueForOption(codecOption);
            var preserveRoot = ctx.ParseResult.GetValueForOption(preserveRootOption);
            var dbPath = ctx.ParseResult.GetValueForOption(dbOption);
            var asJson = ctx.ParseResult.GetValueForOption(jsonOption);

            try
            {
                var mode = explicitMode ?? (string.IsNullOrWhiteSpace(output)
                    ? DestinationMode.SideBySide
                    : DestinationMode.CustomDirectory);

                var resolver = new DestinationResolver();
                var resolved = resolver.ResolveAndValidatePack(
                    inputs,
                    mode,
                    conflict,
                    output);

                if (resolved.ShouldSkipExisting)
                {
                    if (asJson)
                    {
                        ctx.Console.Out.WriteLine(JsonSerializer.Serialize(new
                        {
                            skippedExisting = true,
                            archivePath = resolved.FinalArchivePath,
                        }, CliJsonOptions));
                    }
                    else
                    {
                        ctx.Console.Out.WriteLine($"Skipped existing archive: {resolved.FinalArchivePath}");
                    }

                    ctx.ExitCode = 0;
                    return;
                }

                var writer = new UcxArchiveWriter();
                var result = await writer.CreateAsync(
                    resolved.FinalArchivePath,
                    inputs,
                    new UcxPackOptions
                    {
                        Profile = profile,
                        PreferredCodecOverride = codec,
                        OverwriteDestination = resolved.AllowOverwrite || !File.Exists(resolved.FinalArchivePath),
                        PreserveRootFolderName = preserveRoot,
                        StagingPartPath = resolved.StagingPartPath,
                    },
                    cancellationToken: ctx.GetCancellationToken()).ConfigureAwait(false);

                double durationSec = Math.Max(result.Elapsed.TotalSeconds, 0.001);
                double mbps = result.Summary.OriginalBytes > 0
                    ? (result.Summary.OriginalBytes / (1024.0 * 1024.0)) / durationSec
                    : 0.0;

                using (var history = new SqliteCompressionHistoryStore(dbPath))
                {
                    history.Record(new CompressionHistoryRecord
                    {
                        Operation = "Pack",
                        ArchivePath = result.ArchivePath,
                        SourceSummary = inputs.Length == 1 ? Path.GetFullPath(inputs[0]) : $"{Path.GetFullPath(inputs[0])} (+{inputs.Length - 1} more)",
                        DestinationPath = result.ArchivePath,
                        Profile = profile.ToString(),
                        FileCount = result.Summary.FileCount,
                        OriginalBytes = result.Summary.OriginalBytes,
                        CompressedBytes = result.Summary.ArchiveBytes,
                        SavedBytes = Math.Max(0, result.Summary.OriginalBytes - result.Summary.ArchiveBytes),
                        CompressionRatio = result.Summary.OriginalBytes == 0
                            ? 1.0
                            : (double)result.Summary.ArchiveBytes / result.Summary.OriginalBytes,
                        DuplicateCount = result.Summary.DuplicateCount,
                        DuplicateBytesSaved = result.Summary.DuplicateBytesSaved,
                        DurationMs = Math.Max(1, (long)result.Elapsed.TotalMilliseconds),
                        ThroughputMbPerSec = Math.Round(mbps, 2),
                    });
                }

                if (asJson)
                {
                    ctx.Console.Out.WriteLine(JsonSerializer.Serialize(result, CliJsonOptions));
                }
                else
                {
                    ctx.Console.Out.WriteLine($"Archive:     {result.ArchivePath}");
                    ctx.Console.Out.WriteLine($"Profile:     {profile}");
                    ctx.Console.Out.WriteLine($"Entries:     {result.Summary.FileCount} file(s), {result.Summary.DirectoryCount} dir(s) across {result.Summary.BlockCount} block(s)");
                    ctx.Console.Out.WriteLine($"Size:        {result.Summary.OriginalBytes} -> {result.Summary.ArchiveBytes} bytes ({result.Summary.SpaceSavedPercent:F1}% payload saved)");
                    ctx.Console.Out.WriteLine($"Duplicates:  {result.Summary.DuplicateCount} duplicate file(s) deduplicated ({result.Summary.DuplicateBytesSaved} bytes saved)");
                    foreach (var blk in result.Blocks)
                    {
                        ctx.Console.Out.WriteLine($"  Block #{blk.BlockIndex}: [{blk.ContentClass}] {blk.Codec} (lvl {blk.CodecLevel}) — {blk.UncompressedLength} -> {blk.CompressedLength} B | {blk.CodecRationale}");
                    }
                }

                ctx.ExitCode = 0;
            }
            catch (Exception ex)
            {
                WriteFormattedError(ctx, ex, output ?? inputs.FirstOrDefault());
            }
        });

        return cmd;
    }

    private static Command BuildListCommand()
    {
        var archiveOption = new Option<string>(
            name: "--archive",
            description: "Path to .ucx archive")
        { IsRequired = true };
        archiveOption.AddAlias("-a");
        archiveOption.AddAlias("-i");

        var selectOption = new Option<string[]?>(
            name: "--select",
            description: "Optional file, subfolder, or glob filter patterns")
        { AllowMultipleArgumentsPerToken = true };
        selectOption.AddAlias("-s");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Emit full O(1) manifest inspection as JSON");

        var cmd = new Command("list", "Browse .ucx archive manifest in O(1) without reading payload blocks")
        {
            archiveOption,
            selectOption,
            jsonOption,
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var archivePath = ctx.ParseResult.GetValueForOption(archiveOption)!;
            var select = ctx.ParseResult.GetValueForOption(selectOption);
            var asJson = ctx.ParseResult.GetValueForOption(jsonOption);

            try
            {
                var reader = new UcxArchiveReader();
                var inspection = await reader.InspectArchiveAsync(archivePath, ctx.GetCancellationToken())
                    .ConfigureAwait(false);

                var entries = select is { Length: > 0 }
                    ? inspection.Entries.Where(e => UcxPathSecurity.MatchesSelectPatterns(e.RelativePath, select)).ToList()
                    : inspection.Entries;

                if (asJson)
                {
                    ctx.Console.Out.WriteLine(JsonSerializer.Serialize(new
                    {
                        inspection.ArchivePath,
                        inspection.FormatVersion,
                        inspection.Profile,
                        inspection.Summary,
                        inspection.Blocks,
                        Entries = entries,
                        inspection.RootTree,
                    }, CliJsonOptions));
                }
                else
                {
                    ctx.Console.Out.WriteLine($"Archive:     {inspection.ArchivePath} ({inspection.FormatVersion}, Profile={inspection.Profile})");
                    ctx.Console.Out.WriteLine($"Summary:     {inspection.Summary.FileCount} file(s), {inspection.Summary.OriginalBytes} -> {inspection.Summary.ArchiveBytes} B, Duplicates={inspection.Summary.DuplicateCount} ({inspection.Summary.DuplicateBytesSaved} B saved)");
                    foreach (var entry in entries)
                    {
                        var kind = entry.IsDirectory ? "DIR " : (entry.IsDuplicate ? "DUP " : "FILE");
                        ctx.Console.Out.WriteLine($"{kind}  {entry.OriginalSize,10} -> {entry.EffectiveCompressedSize,10}  [{entry.Codec,-7}]  {entry.RelativePath}");
                    }
                }

                ctx.ExitCode = 0;
            }
            catch (Exception ex)
            {
                WriteFormattedError(ctx, ex, archivePath);
            }
        });

        return cmd;
    }

    private static Command BuildExtractCommand()
    {
        var archiveOption = new Option<string>(
            name: "--archive",
            description: "Path to .ucx archive to extract")
        { IsRequired = true };
        archiveOption.AddAlias("-a");
        archiveOption.AddAlias("-i");

        var outputOption = new Option<string?>(
            name: "--output",
            description: "Destination directory");
        outputOption.AddAlias("-o");

        var selectOption = new Option<string[]?>(
            name: "--select",
            description: "Selective file paths, subfolder prefixes, or glob patterns (e.g. 'src/**', '*.json')")
        { AllowMultipleArgumentsPerToken = true };
        selectOption.AddAlias("-s");

        var destModeOption = new Option<DestinationMode?>(
            name: "--dest-mode",
            description: "Destination mode (CustomDirectory, SideBySide, ArchiveSubfolder)");

        var conflictOption = new Option<DestinationConflictPolicy>(
            name: "--conflict",
            getDefaultValue: () => DestinationConflictPolicy.Fail,
            description: "File conflict policy (Fail, Overwrite, RenameNumbered, SkipExisting)");

        var dbOption = new Option<string?>(
            name: "--db",
            description: "Optional SQLite history/jobs database path");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Emit structured JSON extraction summary");

        var cmd = new Command("extract", "Extract all or selected entries from a .ucx archive with SHA-256 verification")
        {
            archiveOption,
            outputOption,
            selectOption,
            destModeOption,
            conflictOption,
            dbOption,
            jsonOption,
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var archivePath = ctx.ParseResult.GetValueForOption(archiveOption)!;
            var outputDir = ctx.ParseResult.GetValueForOption(outputOption);
            var select = ctx.ParseResult.GetValueForOption(selectOption);
            var explicitMode = ctx.ParseResult.GetValueForOption(destModeOption);
            var conflict = ctx.ParseResult.GetValueForOption(conflictOption);
            var dbPath = ctx.ParseResult.GetValueForOption(dbOption);
            var asJson = ctx.ParseResult.GetValueForOption(jsonOption);

            try
            {
                var reader = new UcxArchiveReader();
                var inspection = await reader.InspectArchiveAsync(archivePath, ctx.GetCancellationToken())
                    .ConfigureAwait(false);

                long requiredBytes = inspection.Entries
                    .Where(e => !e.IsDirectory && UcxPathSecurity.MatchesSelectPatterns(e.RelativePath, select))
                    .Sum(e => e.OriginalSize);

                var mode = explicitMode ?? (string.IsNullOrWhiteSpace(outputDir)
                    ? DestinationMode.ArchiveSubfolder
                    : DestinationMode.CustomDirectory);

                var resolver = new DestinationResolver();
                var resolved = resolver.ResolveAndValidateExtract(
                    archivePath,
                    requiredBytes,
                    mode,
                    outputDir);

                var extractResult = await reader.ExtractEntriesAsync(
                    archivePath,
                    resolved.DestinationDirectory,
                    new UcxExtractOptions
                    {
                        SelectPatterns = select,
                        ConflictPolicy = conflict,
                    },
                    cancellationToken: ctx.GetCancellationToken()).ConfigureAwait(false);

                double durationSec = Math.Max(extractResult.Elapsed.TotalSeconds, 0.001);
                double mbps = extractResult.ExtractedBytes > 0
                    ? (extractResult.ExtractedBytes / (1024.0 * 1024.0)) / durationSec
                    : 0.0;

                using (var history = new SqliteCompressionHistoryStore(dbPath))
                {
                    history.Record(new CompressionHistoryRecord
                    {
                        Operation = "Extract",
                        ArchivePath = Path.GetFullPath(archivePath),
                        SourceSummary = Path.GetFullPath(archivePath),
                        DestinationPath = extractResult.DestinationDirectory,
                        Profile = inspection.Profile.ToString(),
                        FileCount = extractResult.ExtractedFiles,
                        OriginalBytes = extractResult.ExtractedBytes,
                        CompressedBytes = inspection.ArchiveFileBytes,
                        SavedBytes = 0,
                        CompressionRatio = extractResult.ExtractedBytes == 0
                            ? 1.0
                            : (double)inspection.ArchiveFileBytes / extractResult.ExtractedBytes,
                        DurationMs = Math.Max(1, (long)extractResult.Elapsed.TotalMilliseconds),
                        ThroughputMbPerSec = Math.Round(mbps, 2),
                    });
                }

                if (asJson)
                {
                    ctx.Console.Out.WriteLine(JsonSerializer.Serialize(extractResult, CliJsonOptions));
                }
                else
                {
                    ctx.Console.Out.WriteLine($"Destination: {extractResult.DestinationDirectory}");
                    ctx.Console.Out.WriteLine($"Extracted:   {extractResult.ExtractedFiles} file(s), {extractResult.ExtractedBytes} bytes ({extractResult.DecompressedBlockCount} block(s) read, {extractResult.SkippedExistingFiles} skipped)");
                }

                ctx.ExitCode = 0;
            }
            catch (Exception ex)
            {
                WriteFormattedError(ctx, ex, outputDir ?? archivePath);
            }
        });

        return cmd;
    }

    private static Command BuildVerifyCommand()
    {
        var archiveOption = new Option<string>(
            name: "--archive",
            description: "Path to .ucx archive to verify")
        { IsRequired = true };
        archiveOption.AddAlias("-a");
        archiveOption.AddAlias("-i");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Emit structured JSON verification result");

        var cmd = new Command("verify", "Verify .ucx header, manifest, sub-blocks, and all entry SHA-256 checksums")
        {
            archiveOption,
            jsonOption,
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var archivePath = ctx.ParseResult.GetValueForOption(archiveOption)!;
            var asJson = ctx.ParseResult.GetValueForOption(jsonOption);

            try
            {
                var reader = new UcxArchiveReader();
                var verifyResult = await reader.VerifyArchiveAsync(
                    archivePath,
                    cancellationToken: ctx.GetCancellationToken()).ConfigureAwait(false);

                if (asJson)
                {
                    ctx.Console.Out.WriteLine(JsonSerializer.Serialize(verifyResult, CliJsonOptions));
                }
                else
                {
                    ctx.Console.Out.WriteLine($"Verify OK:   {verifyResult.Message}");
                }

                ctx.ExitCode = verifyResult.IsValid ? 0 : 2;
            }
            catch (Exception ex)
            {
                WriteFormattedError(ctx, ex, archivePath);
            }
        });

        return cmd;
    }

    private static Command BuildHistoryCommand()
    {
        var limitOption = new Option<int>(
            name: "--limit",
            getDefaultValue: () => 25,
            description: "Maximum number of recent history entries to display");
        limitOption.AddAlias("-n");

        var operationOption = new Option<string?>(
            name: "--operation",
            description: "Optional operation filter (Pack or Extract)");

        var dbOption = new Option<string?>(
            name: "--db",
            description: "Optional SQLite history/jobs database path");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Emit history records and cumulative summary as JSON");

        var cmd = new Command("history", "Show persistent SQLite compression & extraction history")
        {
            limitOption,
            operationOption,
            dbOption,
            jsonOption,
        };

        cmd.SetHandler((InvocationContext ctx) =>
        {
            var limit = ctx.ParseResult.GetValueForOption(limitOption);
            var opFilter = ctx.ParseResult.GetValueForOption(operationOption);
            var dbPath = ctx.ParseResult.GetValueForOption(dbOption);
            var asJson = ctx.ParseResult.GetValueForOption(jsonOption);

            try
            {
                using var store = new SqliteCompressionHistoryStore(dbPath);
                var entries = store.ListRecent(limit, opFilter);
                var summary = store.GetSummary();

                if (asJson)
                {
                    ctx.Console.Out.WriteLine(JsonSerializer.Serialize(new
                    {
                        Summary = summary,
                        Entries = entries,
                    }, CliJsonOptions));
                }
                else
                {
                    ctx.Console.Out.WriteLine($"History ({entries.Count} shown / {summary.TotalOperations} total, {summary.CumulativeSavedBytes} bytes saved overall):");
                    foreach (var row in entries)
                    {
                        ctx.Console.Out.WriteLine(
                            $"{row.CompletedUtc:yyyy-MM-dd HH:mm:ss}Z  [{row.Operation,-7}]  {row.OriginalBytes,10} -> {row.CompressedBytes,10} B  (saved {row.SavedBytes} B, {row.DurationMs} ms)  {row.ArchivePath}");
                    }
                }

                ctx.ExitCode = 0;
            }
            catch (Exception ex)
            {
                WriteFormattedError(ctx, ex, dbPath);
            }
        });

        return cmd;
    }

    private static void WriteFormattedError(InvocationContext ctx, Exception ex, string? contextPath)
    {
        var err = UltraCompressErrors.MapException(ex, contextPath);
        ctx.Console.Error.WriteLine($"Error [{err.SymbolicCode}] {err.Title}: {err.Message}");
        ctx.Console.Error.WriteLine($"Remediation: {err.Remediation}");
        ctx.ExitCode = MapExitCode(err.Code);
    }

    private static int MapExitCode(UltraCompressErrorCode code) => code switch
    {
        UltraCompressErrorCode.InputNotFound or UltraCompressErrorCode.EmptyInputSet => 3,
        UltraCompressErrorCode.PermissionDenied => 4,
        UltraCompressErrorCode.InsufficientDiskSpace => 5,
        UltraCompressErrorCode.DestinationAlreadyExists => 6,
        UltraCompressErrorCode.CorruptArchive => 7,
        UltraCompressErrorCode.ChecksumMismatch => 8,
        UltraCompressErrorCode.PathTraversalDetected => 9,
        UltraCompressErrorCode.JobCancelled => 130,
        _ => 1,
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        return opts;
    }

    private sealed class BufferedConsole : IConsole
    {
        private readonly BufferedStreamWriter _out = new();
        private readonly BufferedStreamWriter _err = new();

        public IStandardStreamWriter Out => _out;
        public bool IsOutputRedirected => true;
        public IStandardStreamWriter Error => _err;
        public bool IsErrorRedirected => true;
        public bool IsInputRedirected => true;

        public string OutText => _out.ToString();
        public string ErrorText => _err.ToString();
    }

    private sealed class BufferedStreamWriter : IStandardStreamWriter
    {
        private readonly System.Text.StringBuilder _sb = new();

        public void Write(string? value)
        {
            if (value is not null)
            {
                _sb.Append(value);
            }
        }

        public override string ToString() => _sb.ToString();
    }
}
