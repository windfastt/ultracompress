namespace UltraCompress.ArchiveEngine.Tests;

internal sealed class TempTestWorkspace : IDisposable
{
    public TempTestWorkspace()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "ucx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string SubPath(params string[] segments) =>
        Path.Combine([RootPath, .. segments]);

    public string WriteTextFile(string relativePath, string content, DateTimeOffset? mtimeUtc = null)
    {
        var full = SubPath(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (mtimeUtc is not null)
        {
            File.SetLastWriteTimeUtc(full, mtimeUtc.Value.UtcDateTime);
        }

        return full;
    }

    public string WriteBinaryFile(string relativePath, byte[] bytes, DateTimeOffset? mtimeUtc = null)
    {
        var full = SubPath(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        if (mtimeUtc is not null)
        {
            File.SetLastWriteTimeUtc(full, mtimeUtc.Value.UtcDateTime);
        }

        return full;
    }

    public static byte[] CreateSyntheticJpegBytes(int length, int seed = 42)
    {
        var rng = new Random(seed);
        var buf = new byte[Math.Max(length, 16)];
        rng.NextBytes(buf);
        // JPEG SOI + APP0 header magic FF D8 FF E0
        buf[0] = 0xFF;
        buf[1] = 0xD8;
        buf[2] = 0xFF;
        buf[3] = 0xE0;
        return buf;
    }

    public static byte[] CreateSyntheticMp4Bytes(int length, int seed = 99)
    {
        var rng = new Random(seed);
        var buf = new byte[Math.Max(length, 32)];
        rng.NextBytes(buf);
        // ISO BMFF ftyp box: [00 00 00 18]['ftyp']['isom']
        buf[0] = 0x00;
        buf[1] = 0x00;
        buf[2] = 0x00;
        buf[3] = 0x18;
        buf[4] = (byte)'f';
        buf[5] = (byte)'t';
        buf[6] = (byte)'y';
        buf[7] = (byte)'p';
        buf[8] = (byte)'i';
        buf[9] = (byte)'s';
        buf[10] = (byte)'o';
        buf[11] = (byte)'m';
        return buf;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                // Clear ReadOnly attributes before deleting
                foreach (var fi in new DirectoryInfo(RootPath).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        fi.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }

                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
        }
    }
}
