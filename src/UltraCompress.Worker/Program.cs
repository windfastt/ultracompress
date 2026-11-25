using System.CommandLine;
using UltraCompress.Core;

namespace UltraCompress.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("UltraCompress worker — single-step compression CLI");

        var inputOption = new Option<FileInfo>(
            name: "--input",
            description: "File to compress")
        { IsRequired = true };
        inputOption.AddAlias("-i");

        var profileOption = new Option<CompressionProfile>(
            name: "--profile",
            getDefaultValue: () => CompressionProfile.Balanced,
            description: "Compression profile (Instant, Balanced, Archive, Tiny, Laboratory)")
        { IsRequired = false };
        profileOption.AddAlias("-p");

        var outputOption = new Option<FileInfo?>(
            name: "--output",
            description: "Output path (default: &lt;input&gt;.uc.zst / .uc.br / .uc.deflate / .uc.7z by codec)")
        { IsRequired = false };
        outputOption.AddAlias("-o");

        var compressCommand = new Command("compress", "Compress a single file using the profile default recipe")
        {
            inputOption,
            profileOption,
            outputOption,
        };

        compressCommand.SetHandler(async (FileInfo input, CompressionProfile profile, FileInfo? output) =>
        {
            var runner = new CompressionJobRunner();
            var result = await runner.RunAsync(
                input.FullName,
                profile,
                output?.FullName,
                CancellationToken.None);

            Console.WriteLine($"Input:      {result.Fingerprint.Path}");
            Console.WriteLine($"Profile:    {profile}");
            Console.WriteLine($"Recipe:     {result.Recipe.Id}");
            Console.WriteLine($"Output:     {result.OutputPath}");
            Console.WriteLine($"Size:       {result.OriginalBytes} -> {result.CompressedBytes} ({result.Ratio:P1})");
            Console.WriteLine($"Verify:     {result.Verification.Message}");

            Environment.ExitCode = result.Verification.Passed ? 0 : 2;
        }, inputOption, profileOption, outputOption);

        root.AddCommand(compressCommand);

        return await root.InvokeAsync(args);
    }
}