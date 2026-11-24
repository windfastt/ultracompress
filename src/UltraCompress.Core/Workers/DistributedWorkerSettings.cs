namespace UltraCompress.Core.Workers;

public sealed class DistributedWorkerSettings
{
    public const int DefaultWorkerGrpcPort = 17_833;
    public const int DefaultLeaseTtlSeconds = 120;
    public const int DefaultHeartbeatIntervalSeconds = 30;

    public bool Enabled { get; init; }

    public bool PreferRemoteWorkers { get; init; } = true;

    public bool FallbackToLocalProcess { get; init; } = true;

    public int WorkerGrpcPort { get; init; } = DefaultWorkerGrpcPort;

    public string WorkerListenHost { get; init; } = "0.0.0.0";

    public int LeaseTtlSeconds { get; init; } = DefaultLeaseTtlSeconds;

    public int HeartbeatIntervalSeconds { get; init; } = DefaultHeartbeatIntervalSeconds;

    public string? WorkerTokenFile { get; init; }
}