using UltraCompress.Core;
using UltraCompress.Core.SevenZip;

namespace UltraCompress.Core.Tests;

public class SevenZipCommandLineTests
{
    [Fact]
    public void BuildAddArgs_MapsLaboratoryStyleStep()
    {
        var step = new CompressStep
        {
            Codec = "7z",
            Level = 9,
            Method = "LZMA2",
            Solid = false,
            Threads = 0,
        };

        var args = SevenZipCommandLine.BuildAddArgs(@"C:\out\archive.7z", new[] { @"C:\work\payload.bin" }, step);

        Assert.Equal("a", args[0]);
        Assert.Contains("-m0=LZMA2", args);
        Assert.Contains("-mx=9", args);
        Assert.Contains("-ms=off", args);
        Assert.Contains("-mmt=on", args);
        Assert.Equal(@"C:\out\archive.7z", args[^2]);
        Assert.Equal(@"C:\work\payload.bin", args[^1]);
    }

    [Fact]
    public void BuildAddArgs_SolidArchive_UsesMsOn()
    {
        var step = new CompressStep { Solid = true, Level = 5 };
        var args = SevenZipCommandLine.BuildAddArgs("out.7z", new[] { "a.txt" }, step);
        Assert.Contains("-ms=on", args);
        Assert.Contains("-mx=5", args);
    }

    [Fact]
    public void BuildExtractStdoutArgs_IncludesSoFlag()
    {
        var args = SevenZipCommandLine.BuildExtractStdoutArgs("in.7z", "payload.bin");
        Assert.Equal("e", args[0]);
        Assert.Contains("-so", args);
        Assert.Equal("in.7z", args.Single(a => a.EndsWith(".7z", StringComparison.Ordinal)));
        Assert.Equal("payload.bin", args[^1]);
    }
}