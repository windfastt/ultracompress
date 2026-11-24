using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using UltraCompress.Benchmarks;

if (args.Contains("--ci", StringComparer.OrdinalIgnoreCase))
{
    BenchmarkRunner.Run<JobQueueBenchmarks>(new DebugInProcessConfig());
    return;
}
BenchmarkRunner.Run<JobQueueBenchmarks>();
