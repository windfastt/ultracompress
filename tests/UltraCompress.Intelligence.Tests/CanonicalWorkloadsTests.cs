using System.Text;
using UltraCompress.Core;
using UltraCompress.Core.Intelligence;
using UltraCompress.Core.Safety;

namespace UltraCompress.Intelligence.Tests;

public sealed class CanonicalWorkloadsTests
{
    private const long OneGiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData("MP4", "capture_uhd_10gb.mp4")]
    [InlineData("MKV", "remux_hevc_10gb.mkv")]
    public void Workload1_TenGiBVideo_SelectsStoreFastStreamBypassWithSubPointTwoPercentGainRationale(
        string container,
        string fileName)
    {
        var header = container == "MP4"
            ? MagicSignatureDetectionTests.BuildFtyp("isom", "iso2", "mp41")
            : new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x42, 0x82, 0x88, (byte)'m', (byte)'a', (byte)'t', (byte)'r', (byte)'o', (byte)'s', (byte)'k', (byte)'a' };

        using var stream = new VirtualLargeWorkloadStream(
            VirtualLargeWorkloadStream.TenGiB,
            header,
            VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom);

        var report = StrategySelector.AnalyzeStream(stream, fileName, CompressionProfile.Tiny);

        Assert.Equal(OperationKind.LosslessCompression, report.OperationKind);
        Assert.Equal(container, report.DetectedFormat);
        Assert.Equal(ContentClass.CompressedVideo, report.ContentClass);
        Assert.Equal(WorkloadArchetype.CompressedVideoStream, report.WorkloadArchetype);
        Assert.Equal("store", report.SelectedPipeline.PrimaryCodec);
        Assert.Equal(0, report.SelectedPipeline.CodecLevel);
        Assert.Contains("Store / FastStreamBypass", report.SelectedPipeline.StrategyName);
        Assert.Equal(SpeedTier.DiskSpeedBypass, report.PredictedSpeedTier);
        Assert.True(report.PredictedSavingsPercent < 0.2,
            $"Expected <0.2% predicted lossless gain on high-entropy video, got {report.PredictedSavingsPercent:F3}%.");
        Assert.Contains("<0.2%", report.SelectedPipeline.RationaleSummary);
        Assert.Contains(report.RejectedAlternatives, r => r.StrategyName.Contains("LZMA2", StringComparison.Ordinal) && r.RejectionReason.Contains("<0.2%", StringComparison.Ordinal));
        Assert.Contains(report.RejectedAlternatives, r => r.StrategyName.Contains("Zstd", StringComparison.Ordinal));
        Assert.NotNull(report.ConsentGatedAdvisoryNote);
        Assert.Contains("Non-executed", report.ConsentGatedAdvisoryNote);

        Assert.Equal(5 * 64 * 1024, stream.TotalBytesRead);
        Assert.True(RecipeSchemaValidation.ValidatePipelineRecipe(report.SelectedPipeline.ExecutableRecipe).IsValid);
        SafetyPolicy.ValidateCompressionProfileRecipe(CompressionProfile.Tiny, report.SelectedPipeline.ExecutableRecipe);
    }

    [Theory]
    [InlineData(CompressionProfile.Archive, "zstd")]
    [InlineData(CompressionProfile.Laboratory, "7z")]
    public void Workload2_TenGiBSourceRepository_SelectsTypeGroupedSolidLongWindowAndDedup(
        CompressionProfile profile,
        string expectedCodec)
    {
        var csFiller = VirtualLargeWorkloadStream.CreateRepeatingTextFiller(
            "using System;\nusing System.Collections.Generic;\nnamespace UltraCompress.Engine;\n" +
            "public sealed class ArchiveBlockScheduler {\n" +
            "    public ValueTask<BlockTicket> EnqueueSolidBlockAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => default;\n}\n");

        var tsFiller = VirtualLargeWorkloadStream.CreateRepeatingTextFiller(
            "import { createContext, useMemo } from 'react';\n" +
            "export interface StrategyViewProps { readonly ratio: number; readonly codec: string; }\n" +
            "export function StrategyRationaleCard(props: StrategyViewProps) { return `${props.codec}: ${props.ratio}`; }\n");

        var jsonFiller = VirtualLargeWorkloadStream.CreateRepeatingTextFiller(
            "{\n  \"package\": \"@ultracompress/desktop\",\n  \"version\": \"1.0.0\",\n  \"strictLossless\": true\n}\n");

        var inspections = new List<ContentInspectionResult>();
        using (var s1 = new VirtualLargeWorkloadStream(4 * OneGiB, Encoding.UTF8.GetBytes("// C# backend modules\n"), csFiller))
            inspections.Add(ContentInspector.Inspect(s1, "src/Backend/ModuleBundleA.cs"));
        using (var s2 = new VirtualLargeWorkloadStream(3 * OneGiB, Encoding.UTF8.GetBytes("// TS frontend modules\n"), tsFiller))
            inspections.Add(ContentInspector.Inspect(s2, "web/src/ComponentsBundle.tsx"));
        using (var s3 = new VirtualLargeWorkloadStream(2 * OneGiB, Encoding.UTF8.GetBytes("// C# duplicate vendor tree\n"), csFiller))
            inspections.Add(ContentInspector.Inspect(s3, "vendor/mirrorA/GeneratedContracts.cs"));
        using (var s4 = new VirtualLargeWorkloadStream(1 * OneGiB - (64 * 1024), Encoding.UTF8.GetBytes("{\n  \"lockfileVersion\": 3,\n"), jsonFiller))
            inspections.Add(ContentInspector.Inspect(s4, "package-lock.json"));

        var licenseBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("MIT License — Copyright (c) UltraCompress Contributors.\n", 600)));
        var dup1 = ContentInspector.InspectBytes(licenseBytes, "packages/core/LICENSE.txt");
        var dup2 = ContentInspector.InspectBytes(licenseBytes, "packages/ui/LICENSE.txt");
        inspections.Add(dup1);
        inspections.Add(dup2);

        var report = StrategySelector.SelectForWorkload("/repos/monorepo-10gb", inspections, profile);

        Assert.Equal(OperationKind.LosslessCompression, report.OperationKind);
        Assert.Equal(WorkloadArchetype.SourceRepository, report.WorkloadArchetype);
        Assert.Equal("type-grouped-solid-longwindow-dedup", report.SelectedPipeline.StrategyId);
        Assert.Equal(expectedCodec, report.SelectedPipeline.PrimaryCodec);
        Assert.True(report.SelectedPipeline.SolidBlockGrouping);
        Assert.True(report.SelectedPipeline.DeduplicateExactFiles);
        Assert.True(report.SelectedPipeline.DictionaryOrWindowSizeKb >= 32768);
        Assert.True(report.DuplicateFileCount >= 1);
        Assert.True(report.DuplicateReclaimableBytes >= licenseBytes.Length);
        Assert.True(report.PredictedRatio < 0.35, $"Expected strong solid source ratio, got {report.PredictedRatio:F3}.");
        Assert.Contains(report.RejectedAlternatives, r => r.StrategyName.Contains("ZIP", StringComparison.OrdinalIgnoreCase));
        Assert.True(RecipeSchemaValidation.ValidatePipelineRecipe(report.SelectedPipeline.ExecutableRecipe).IsValid);
        SafetyPolicy.ValidateCompressionProfileRecipe(profile, report.SelectedPipeline.ExecutableRecipe);
    }

    [Theory]
    [InlineData("SQLite", "telemetry_10gb.sqlite")]
    [InlineData("SQL", "pg_dump_10gb.sql")]
    [InlineData("CSV", "ledger_10gb.csv")]
    public void Workload3_TenGiBDatabaseDump_DetectsStrideOrSchemaCorrelationAndSelectsByteShuffleDeltaLongWindow(
        string format,
        string fileName)
    {
        byte[] header;
        InteriorSpanFiller filler;

        if (format == "SQLite")
        {
            header = MagicSignatureDetectionTests.BuildSqliteHeader(pageSize: 4096);
            filler = VirtualLargeWorkloadStream.FillStructuredDatabasePages;
        }
        else if (format == "SQL")
        {
            header = Encoding.UTF8.GetBytes("-- PostgreSQL database dump\nCREATE TABLE ledger_events (id BIGINT PRIMARY KEY, account_id BIGINT, amount_cents BIGINT, created_at TEXT);\n");
            filler = VirtualLargeWorkloadStream.CreateRepeatingTextFiller(
                "INSERT INTO ledger_events (id, account_id, amount_cents, created_at) VALUES (1000451, 88412, 249900, '2026-11-24T19:00:00Z');\n" +
                "INSERT INTO ledger_events (id, account_id, amount_cents, created_at) VALUES (1000452, 88412, 129900, '2026-11-24T19:00:01Z');\n");
        }
        else
        {
            header = Encoding.UTF8.GetBytes("event_id,account_id,region,latency_us,bytes_transferred,status_code\n");
            filler = VirtualLargeWorkloadStream.CreateRepeatingTextFiller(
                "90001234,44102,us-east-1,1420,65536,200\n90001235,44102,us-east-1,1395,65536,200\n90001236,44109,eu-west-1,2110,32768,200\n");
        }

        using var stream = new VirtualLargeWorkloadStream(VirtualLargeWorkloadStream.TenGiB, header, filler);
        var report = StrategySelector.AnalyzeStream(stream, fileName, CompressionProfile.Tiny);

        Assert.Equal(OperationKind.LosslessCompression, report.OperationKind);
        Assert.Equal(format, report.DetectedFormat);
        Assert.Equal(ContentClass.DatabaseAndTabular, report.ContentClass);
        Assert.Equal(WorkloadArchetype.DatabaseDump, report.WorkloadArchetype);
        Assert.Equal("structured-byteshuffle-delta-longwindow", report.SelectedPipeline.StrategyId);
        Assert.Contains("ByteShuffle/Delta", report.SelectedPipeline.StrategyName);
        Assert.Equal("zstd", report.SelectedPipeline.PrimaryCodec);
        Assert.Equal(65536, report.SelectedPipeline.DictionaryOrWindowSizeKb);

        if (format == "SQLite")
        {
            Assert.True(Math.Max(report.Signals.Stride4EntropyReduction, report.Signals.Stride8EntropyReduction) > 1.0,
                "SQLite binary pages should show strong 4/8-byte stride delta entropy reduction.");
        }

        Assert.True(report.PredictedRatio < 0.45);
        Assert.Contains(report.RejectedAlternatives, r => r.StrategyName.Contains("Deflate", StringComparison.OrdinalIgnoreCase));
        Assert.True(RecipeSchemaValidation.ValidatePipelineRecipe(report.SelectedPipeline.ExecutableRecipe).IsValid);
        SafetyPolicy.ValidateCompressionProfileRecipe(CompressionProfile.Tiny, report.SelectedPipeline.ExecutableRecipe);
    }

    [Fact]
    public void Workload4_TenGiBPhotoCollection_SplitsJpegHeicToStoreAndRawBmpTiffToDeltaZstdWithZeroSilentLossyChanges()
    {
        var jpegHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x16, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00, 0x49, 0x49, 0x2A, 0x00 };
        var heicHeader = MagicSignatureDetectionTests.BuildFtyp("heic", "mif1");
        var dngHeader = MagicSignatureDetectionTests.BuildTiffIfdHeader(includeDngTag: true, compression: 1);
        var tiffHeader = MagicSignatureDetectionTests.BuildTiffIfdHeader(includeDngTag: false, compression: 1);
        var bmpHeader = MagicSignatureDetectionTests.BuildBmpHeader(width: 4000, height: 3000, bpp: 24);

        var inspections = new List<ContentInspectionResult>();

        using (var jpegStream = new VirtualLargeWorkloadStream(3 * OneGiB, jpegHeader, VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom))
            inspections.Add(ContentInspector.Inspect(jpegStream, "DCIM/100MEDIA/DSC_0001.JPG"));
        using (var heicStream = new VirtualLargeWorkloadStream(2 * OneGiB, heicHeader, VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom))
            inspections.Add(ContentInspector.Inspect(heicStream, "DCIM/100MEDIA/IMG_0002.HEIC"));

        using (var dngStream = new VirtualLargeWorkloadStream((long)(2.5 * OneGiB), dngHeader, (off, dst) => VirtualLargeWorkloadStream.FillUncompressedSensorGradient(off, dst, 2)))
            inspections.Add(ContentInspector.Inspect(dngStream, "Studio/RAW/SHOT_0001.DNG"));
        using (var tiffStream = new VirtualLargeWorkloadStream((long)(1.5 * OneGiB), tiffHeader, (off, dst) => VirtualLargeWorkloadStream.FillUncompressedSensorGradient(off, dst, 3)))
            inspections.Add(ContentInspector.Inspect(tiffStream, "Studio/Masters/SCAN_0002.TIFF"));
        using (var bmpStream = new VirtualLargeWorkloadStream(1 * OneGiB, bmpHeader, (off, dst) => VirtualLargeWorkloadStream.FillUncompressedSensorGradient(off, dst, 3)))
            inspections.Add(ContentInspector.Inspect(bmpStream, "Studio/Captures/FRAME_0003.BMP"));

        using (var jpegDupStream = new VirtualLargeWorkloadStream(128 * 1024, jpegHeader, VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom))
        {
            inspections.Add(ContentInspector.Inspect(jpegDupStream, "DCIM/Burst/BURST_01.JPG"));
            jpegDupStream.Position = 0;
            inspections.Add(ContentInspector.Inspect(jpegDupStream, "DCIM/Backup/BURST_01_COPY.JPG"));
        }

        var report = StrategySelector.SelectForWorkload("/Photos/WeddingShoot_10GB", inspections, CompressionProfile.Archive);

        Assert.Equal(OperationKind.LosslessCompression, report.OperationKind);
        Assert.Equal(ContentClass.HeterogeneousWorkload, report.ContentClass);
        Assert.Equal(WorkloadArchetype.HeterogeneousPhotoCollection, report.WorkloadArchetype);
        Assert.Equal("heterogeneous-photo-split", report.SelectedPipeline.StrategyId);
        Assert.True(report.SelectedPipeline.DeduplicateExactFiles);
        Assert.Equal(1, report.DuplicateFileCount);

        Assert.Equal(2, report.Routes.Count);

        var storeRoute = Assert.Single(report.Routes, r => r.Codec == "store");
        Assert.Contains("JPEG", storeRoute.Formats);
        Assert.Contains("HEIC", storeRoute.Formats);
        Assert.Equal(SpeedTier.DiskSpeedBypass, storeRoute.SpeedTier);

        var deltaRoute = Assert.Single(report.Routes, r => r.Codec == "zstd");
        Assert.Contains("DNG", deltaRoute.Formats);
        Assert.Contains("TIFF", deltaRoute.Formats);
        Assert.Contains("BMP", deltaRoute.Formats);
        Assert.Contains(deltaRoute.Preprocessors, p => p.Contains("delta", StringComparison.OrdinalIgnoreCase));
        Assert.True(deltaRoute.PredictedRatio < 0.65);

        Assert.Contains(report.RejectedAlternatives, r => r.ReasonCode == "SILENT_LOSSY_AND_DATA_REMOVAL_FORBIDDEN");
        Assert.NotNull(report.ConsentGatedAdvisoryNote);
        Assert.Contains("EXIF", report.ConsentGatedAdvisoryNote);
        Assert.Contains("Non-executed", report.ConsentGatedAdvisoryNote);
        Assert.True(RecipeSchemaValidation.ValidatePipelineRecipe(report.SelectedPipeline.ExecutableRecipe).IsValid);
        SafetyPolicy.ValidateCompressionProfileRecipe(CompressionProfile.Archive, report.SelectedPipeline.ExecutableRecipe);
    }

    [Fact]
    public void AllFourCanonicalWorkloads_ProduceFourDistinctArchetypesAndStrategyIds()
    {
        using var videoStream = new VirtualLargeWorkloadStream(
            VirtualLargeWorkloadStream.TenGiB,
            MagicSignatureDetectionTests.BuildFtyp("isom"),
            VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom);
        var videoReport = StrategySelector.AnalyzeStream(videoStream, "film.mp4", CompressionProfile.Archive);

        var codeInspection = ContentInspector.InspectBytes(
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("namespace Core; public record Node(int Id, string Name);\n", 1200))),
            "Node.cs");
        var repoReport = StrategySelector.SelectForWorkload("/repo", new[] { codeInspection, codeInspection }, CompressionProfile.Archive);

        using var dbStream = new VirtualLargeWorkloadStream(
            VirtualLargeWorkloadStream.TenGiB,
            MagicSignatureDetectionTests.BuildSqliteHeader(4096),
            VirtualLargeWorkloadStream.FillStructuredDatabasePages);
        var dbReport = StrategySelector.AnalyzeStream(dbStream, "warehouse.sqlite", CompressionProfile.Archive);

        using var jpegStream = new VirtualLargeWorkloadStream(
            5 * OneGiB,
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 },
            VirtualLargeWorkloadStream.FillHighEntropyPseudoRandom);
        using var bmpStream = new VirtualLargeWorkloadStream(
            5 * OneGiB,
            MagicSignatureDetectionTests.BuildBmpHeader(2000, 2000, 24),
            (off, dst) => VirtualLargeWorkloadStream.FillUncompressedSensorGradient(off, dst, 3));
        var photoReport = StrategySelector.SelectForWorkload(
            "/photos",
            new[] { ContentInspector.Inspect(jpegStream, "a.jpg"), ContentInspector.Inspect(bmpStream, "b.bmp") },
            CompressionProfile.Archive);

        var reports = new[] { videoReport, repoReport, dbReport, photoReport };
        Assert.All(reports, r => Assert.Equal(OperationKind.LosslessCompression, r.OperationKind));

        var archetypes = reports.Select(r => r.WorkloadArchetype).Distinct().Count();
        var strategyIds = reports.Select(r => r.SelectedPipeline.StrategyId).Distinct().Count();

        Assert.Equal(4, archetypes);
        Assert.Equal(4, strategyIds);
    }
}
