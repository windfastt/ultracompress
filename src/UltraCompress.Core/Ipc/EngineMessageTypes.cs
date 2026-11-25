namespace UltraCompress.Core.Ipc;

public static class EngineMessageTypes
{
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string GetStatus = "getStatus";
    public const string Status = "status";
    public const string PauseAll = "pauseAll";
    public const string ResumeAll = "resumeAll";
    public const string ListJobs = "listJobs";
    public const string EnqueueJob = "enqueueJob";
    public const string CancelJob = "cancelJob";
    public const string JobList = "jobList";
    public const string Ack = "ack";
    public const string Error = "error";

    // Track 3: .ucx Archive Manager & Resilient Queue IPC operations
    public const string Pack = "pack";
    public const string ListArchive = "list";
    public const string Extract = "extract";
    public const string Verify = "verify";
    public const string History = "history";
    public const string GetArchiveJob = "getArchiveJob";
    public const string PauseJob = "pauseJob";
    public const string ResumeJob = "resumeJob";
    public const string RollbackJob = "rollbackJob";
    public const string ListInterruptedJobs = "listInterruptedJobs";

    public const string ArchiveInspectionResult = "archiveInspection";
    public const string VerifyResult = "verifyResult";
    public const string HistoryResult = "historyResult";
    public const string ArchiveJobResult = "archiveJob";
    public const string InterruptedJobsResult = "interruptedJobs";
}
