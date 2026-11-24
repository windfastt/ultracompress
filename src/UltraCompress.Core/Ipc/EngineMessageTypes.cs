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
}