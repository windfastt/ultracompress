using UltraCompress.Core;
using UltraCompress.Core.Jobs;
using ProtoStatus = UltraCompress.Engine.Grpc.CompressionJobStatus;

namespace UltraCompress.Engine.Grpc;

internal static class JobMapper
{
    public static Grpc.JobSnapshot ToSnapshot(CompressionJobRecord job) =>
        new()
        {
            JobId = job.JobId.ToString("D"),
            InputPath = job.InputPath,
            OutputPath = job.OutputPath ?? string.Empty,
            Profile = job.Profile.ToString(),
            Status = ToProtoStatus(job.Status),
            CreatedAtUnixMs = job.CreatedAt.ToUnixTimeMilliseconds(),
            StartedAtUnixMs = job.StartedAt?.ToUnixTimeMilliseconds() ?? 0,
            CompletedAtUnixMs = job.CompletedAt?.ToUnixTimeMilliseconds() ?? 0,
            ErrorMessage = job.ErrorMessage ?? string.Empty,
            OriginalBytes = job.OriginalBytes ?? 0,
            CompressedBytes = job.CompressedBytes ?? 0,
        };

    public static CompressionProfile ParseProfile(string profile) =>
        CompressionProfileParser.ParseOrDefault(profile, CompressionProfile.Balanced);

    public static ProtoStatus ToProtoStatus(JobStatus status) => status switch
    {
        JobStatus.Queued => ProtoStatus.Queued,
        JobStatus.Running => ProtoStatus.Running,
        JobStatus.Completed => ProtoStatus.Completed,
        JobStatus.Failed => ProtoStatus.Failed,
        JobStatus.Cancelled => ProtoStatus.Cancelled,
        _ => ProtoStatus.Unspecified,
    };
}