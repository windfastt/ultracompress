using System.Text;
using UltraCompress.Benchmarks.Corpus;
using UltraCompress.Benchmarks.Engine;
using UltraCompress.Benchmarks.Models;

namespace UltraCompress.Benchmarks.Tests;

public sealed class CorpusDeterminismTests
{
    [Fact]
    public void BuildAll_SmokeScale_IsByteIdenticalAcrossRuns_AndMeetsCategoryInvariants()
    {
        string dirA = Path.Combine(Path.GetTempPath(), $"uc_test_corp_a_{Guid.NewGuid():N}");
        string dirB = Path.Combine(Path.GetTempPath(), $"uc_test_corp_b_{Guid.NewGuid():N}");

        try
        {
            var catsA = CorpusBuilder.BuildAll(dirA, BenchmarkScale.Smoke);
            var catsB = CorpusBuilder.BuildAll(dirB, BenchmarkScale.Smoke);

            Assert.Equal(6, catsA.Count);
            Assert.Equal(
                CorpusBuilder.ComputeAggregateCorpusSha256(catsA),
                CorpusBuilder.ComputeAggregateCorpusSha256(catsB));

            for (int i = 0; i < catsA.Count; i++)
            {
                Assert.Equal(catsA[i].Category, catsB[i].Category);
                Assert.Equal(catsA[i].OriginalBytes, catsB[i].OriginalBytes);
                Assert.Equal(catsA[i].ManifestSha256, catsB[i].ManifestSha256);
                Assert.Equal(catsA[i].TarSha256, catsB[i].TarSha256);
            }

            var byName = catsA.ToDictionary(c => c.Category, StringComparer.Ordinal);

            // 1. video-stream: MP4 header + ~7.99 bpb high entropy
            var video = byName[CorpusBuilder.VideoStreamCategory];
            Assert.InRange(video.MeanEntropyBitsPerByte, 7.98, 8.0);
            var firstMp4 = Path.Combine(video.DirectoryPath, video.Files[0].RelativePath);
            byte[] mp4Head = File.ReadAllBytes(firstMp4)[..16];
            Assert.Equal("ftypisom", Encoding.ASCII.GetString(mp4Head, 4, 8));

            // 2. source-repo: C#/TS/JSON/MD + exact duplicate vendored files
            var repo = byName[CorpusBuilder.SourceRepoCategory];
            Assert.True(repo.DuplicateFileCount >= 4);
            Assert.Contains(repo.Files, f => f.RelativePath.EndsWith(".cs", StringComparison.Ordinal));
            Assert.Contains(repo.Files, f => f.RelativePath.EndsWith(".tsx", StringComparison.Ordinal));
            Assert.Contains(repo.Files, f => f.RelativePath.EndsWith(".json", StringComparison.Ordinal));
            Assert.Contains(repo.Files, f => f.RelativePath.EndsWith(".md", StringComparison.Ordinal));

            // 3. database-dump: SQL + CSV + 8-byte & 32-byte stride numeric pages
            var db = byName[CorpusBuilder.DatabaseDumpCategory];
            var col8Entry = Assert.Single(db.Files, f => f.RelativePath.EndsWith("timeseries_columnar_8b.bin", StringComparison.Ordinal));
            byte[] col8Bin = File.ReadAllBytes(Path.Combine(db.DirectoryPath, col8Entry.RelativePath));
            Assert.Equal(TransformKind.ByteShuffle8Delta, ReversibleTransforms.DetectBestNumericOrRasterTransform(col8Bin));

            var row32Entry = Assert.Single(db.Files, f => f.RelativePath.EndsWith("timeseries_metrics_pages.bin", StringComparison.Ordinal));
            byte[] row32Bin = File.ReadAllBytes(Path.Combine(db.DirectoryPath, row32Entry.RelativePath));
            Assert.Equal(TransformKind.ByteShuffle32Delta, ReversibleTransforms.DetectBestNumericOrRasterTransform(row32Bin));

            // 4. photo-collection: JPEG + BMP + TIFF + duplicates
            var photos = byName[CorpusBuilder.PhotoCollectionCategory];
            Assert.True(photos.DuplicateFileCount >= 3);
            Assert.Contains(photos.Files, f => f.RelativePath.EndsWith(".jpg", StringComparison.Ordinal) && f.EntropyBitsPerByte >= 7.95);
            Assert.Contains(photos.Files, f => f.RelativePath.EndsWith(".bmp", StringComparison.Ordinal));
            Assert.Contains(photos.Files, f => f.RelativePath.EndsWith(".tif", StringComparison.Ordinal));

            // 5. native-binaries: PE and ELF headers
            var bins = byName[CorpusBuilder.NativeBinariesCategory];
            var dll = bins.Files.First(f => f.RelativePath.EndsWith(".dll", StringComparison.Ordinal));
            byte[] dllBytes = File.ReadAllBytes(Path.Combine(bins.DirectoryPath, dll.RelativePath));
            Assert.Equal((byte)'M', dllBytes[0]);
            Assert.Equal((byte)'Z', dllBytes[1]);

            var so = bins.Files.First(f => f.RelativePath.EndsWith(".so", StringComparison.Ordinal));
            byte[] soBytes = File.ReadAllBytes(Path.Combine(bins.DirectoryPath, so.RelativePath));
            Assert.Equal(0x7F, soBytes[0]);
            Assert.Equal("ELF", Encoding.ASCII.GetString(soBytes, 1, 3));

            // 6. mixed-workspace: contains all 5 subtrees
            var mixed = byName[CorpusBuilder.MixedWorkspaceCategory];
            Assert.Contains(mixed.Files, f => f.RelativePath.StartsWith("media/", StringComparison.Ordinal));
            Assert.Contains(mixed.Files, f => f.RelativePath.StartsWith("repo/", StringComparison.Ordinal));
            Assert.Contains(mixed.Files, f => f.RelativePath.StartsWith("database/", StringComparison.Ordinal));
            Assert.Contains(mixed.Files, f => f.RelativePath.StartsWith("photos/", StringComparison.Ordinal));
            Assert.Contains(mixed.Files, f => f.RelativePath.StartsWith("binaries/", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dirA)) Directory.Delete(dirA, recursive: true);
            if (Directory.Exists(dirB)) Directory.Delete(dirB, recursive: true);
        }
    }
}
