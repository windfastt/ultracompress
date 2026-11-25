namespace UltraCompress.Worker;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        WorkerCli.InvokeAsync(args);
}
