using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraCompress.Core.Intelligence;

/// <summary>
/// Renders <see cref="StrategyDecisionReport"/> as structured JSON or clean human-readable rationale tables.
/// </summary>
public static class StrategyReportFormatter
{
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        return opts;
    }

    public static string ToJson(StrategyDecisionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static StrategyDecisionReport? FromJson(string json) =>
        JsonSerializer.Deserialize<StrategyDecisionReport>(json, JsonOptions);

    public static string FormatHumanReadable(StrategyDecisionReport report, bool detailedExplain = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder(2048);
        var inv = CultureInfo.InvariantCulture;

        sb.AppendLine("================================================================================");
        sb.AppendLine(" UltraCompress Intelligent Strategy Decision Report");
        sb.AppendLine("================================================================================");
        sb.AppendLine(string.Format(inv, " Target           : {0} ({1})", report.TargetPath, report.IsDirectory ? "Directory Workload" : "Single File"));
        sb.AppendLine(string.Format(inv, " Profile / Mode   : {0} | {1}", report.Profile, report.OperationKind));
        sb.AppendLine(string.Format(inv, " Detected Format  : {0} ({1})", report.DetectedFormat, report.DetectedMimeType));
        sb.AppendLine(string.Format(inv, " Content Class    : {0} [Archetype: {1}]", report.ContentClass, report.WorkloadArchetype));
        sb.AppendLine(string.Format(inv, " Input Volume     : {0} across {1} file(s)", StrategySelector.FormatBytes(report.TotalBytes), report.TotalFiles));
        sb.AppendLine(string.Format(inv, " O(1) Inspection  : {0} sampled in {1:F2} ms (peak buffer {2})",
            StrategySelector.FormatBytes(report.SampledBytes),
            report.InspectionDurationMs,
            StrategySelector.FormatBytes(report.PeakInspectionBufferBytes)));
        if (report.DuplicateFileCount > 0)
        {
            sb.AppendLine(string.Format(inv, " Exact Duplicates : {0} file(s) ({1} reclaimable via SHA-256 dedup)",
                report.DuplicateFileCount,
                StrategySelector.FormatBytes(report.DuplicateReclaimableBytes)));
        }

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(" Selected Lossless Strategy & Outcome Prediction");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(string.Format(inv, " Strategy         : {0} [{1}]", report.SelectedPipeline.StrategyName, report.SelectedPipeline.StrategyId));
        sb.AppendLine(string.Format(inv, " Preprocessors    : {0}", string.Join(" -> ", report.SelectedPipeline.Preprocessors)));
        sb.AppendLine(string.Format(inv, " Primary Codec    : {0} (level {1}{2}{3})",
            report.SelectedPipeline.PrimaryCodec,
            report.SelectedPipeline.CodecLevel,
            report.SelectedPipeline.DictionaryOrWindowSizeKb.HasValue ? $", window {report.SelectedPipeline.DictionaryOrWindowSizeKb.Value / 1024.0:F1} MiB" : string.Empty,
            report.SelectedPipeline.SolidBlockGrouping ? ", solid=true" : string.Empty));
        sb.AppendLine(string.Format(inv, " Predicted Output : {0} ({1:F1}% of original | {2:F1}% lossless savings)",
            StrategySelector.FormatBytes(report.PredictedCompressedBytes),
            report.PredictedRatio * 100.0,
            report.PredictedSavingsPercent));
        sb.AppendLine(string.Format(inv, " Speed Tier       : {0} ({1})", report.PredictedSpeedTier, report.EstimatedThroughputRange));
        sb.AppendLine(string.Format(inv, " Safety Guarantee : {0}", report.LosslessGuarantee));
        sb.AppendLine(string.Format(inv, " Rationale        : {0}", report.SelectedPipeline.RationaleSummary));

        if (detailedExplain)
        {
            var s = report.Signals;
            sb.AppendLine();
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(" Measured Statistical & Micro-Probe Signals");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(" Signal                          | Measured Value");
            sb.AppendLine("---------------------------------+----------------------------------------------");
            sb.AppendLine(string.Format(inv, " Mean / Interior Entropy         | {0:F3} bpb / {1:F3} bpb (var={2:F4}, sd={3:F3})",
                s.MeanEntropyBitsPerByte, s.InteriorEntropyBitsPerByte, s.EntropyVariance, s.EntropyStdDev));
            sb.AppendLine(string.Format(inv, " Bigram Predictability           | {0:P1} (H2 cond={1:F3} bpb)",
                s.BigramPredictability, s.BigramConditionalEntropy));
            sb.AppendLine(string.Format(inv, " Run-Length Score                | {0:P2} (zero-byte share={1:P1})",
                s.RunLengthScore, s.ZeroByteRatio));
            sb.AppendLine(string.Format(inv, " Stride-4 / Stride-8 Delta Gain  | -{0:F3} bpb / -{1:F3} bpb (best={2}B @ -{3:F3} bpb)",
                s.Stride4EntropyReduction, s.Stride8EntropyReduction, s.BestStrideBytes, s.BestStrideEntropyReduction));
            sb.AppendLine(string.Format(inv, " 32 KiB Micro-Probe Ratio        | {0:F4} ({1}",
                s.MicroProbeRatio,
                s.PreprocessedMicroProbeRatio.HasValue
                    ? string.Format(inv, "preprocessed: {0:F4})", s.PreprocessedMicroProbeRatio.Value)
                    : "raw fast probe)"));

            if (report.WindowSamples.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(" Multi-Window O(1) Samples:");
                sb.AppendLine(" Window      | Offset          | Length    | Shannon (bpb) | d4 (bpb) | d8 (bpb)");
                sb.AppendLine("-------------+-----------------+-----------+---------------+----------+---------");
                foreach (var w in report.WindowSamples)
                {
                    sb.AppendLine(string.Format(inv, " {0,-11} | {1,15:N0} | {2,9:N0} | {3,13:F3} | {4,8:F3} | {5,7:F3}",
                        w.Position,
                        w.Offset,
                        w.Length,
                        w.EntropyBitsPerByte,
                        w.Stride4DeltaEntropy,
                        w.Stride8DeltaEntropy));
                }
            }
        }

        if (report.Routes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(" Per-Class Workload Routing Plan");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(" Route                            | Files | Share  | Pipeline                           | Pred Ratio");
            sb.AppendLine("----------------------------------+-------+--------+------------------------------------+-----------");
            foreach (var r in report.Routes)
            {
                sb.AppendLine(string.Format(inv, " {0,-32} | {1,5} | {2,5:F1}% | {3,-34} | {4,8:F1}%",
                    Truncate(r.RouteName, 32),
                    r.FileCount,
                    r.ShareOfBytesPercent,
                    Truncate(r.PipelineSummary, 34),
                    r.PredictedRatio * 100.0));
            }
        }

        if (report.RejectedAlternatives.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(" Rejected Alternatives & Explicit Reasons");
            sb.AppendLine("--------------------------------------------------------------------------------");
            foreach (var alt in report.RejectedAlternatives)
            {
                sb.AppendLine(string.Format(inv, " [REJECTED] {0} ({1})", alt.StrategyName, alt.ReasonCode));
                sb.AppendLine(string.Format(inv, "   Reason  : {0}", alt.RejectionReason));
                sb.AppendLine(string.Format(inv, "   Penalty : {0}", alt.EstimatedPenalty));
            }
        }

        if (!string.IsNullOrWhiteSpace(report.ConsentGatedAdvisoryNote))
        {
            sb.AppendLine();
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(" Consent-Gated Separate Mode Advisory (NOT EXECUTED)");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine(" " + report.ConsentGatedAdvisoryNote);
        }

        sb.AppendLine("================================================================================");
        return sb.ToString();
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..(maxLen - 1)] + "…";
}
