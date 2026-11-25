using BenchmarkDotNet.Attributes;
using UltraCompress.Core;
using UltraCompress.Engine;

namespace UltraCompress.Benchmarks;

[SimpleJob(warmupCount: 1, iterationCount: 2)]
public class JobQueueBenchmarks
{
    private JobQueueService _queue = null!;
    [GlobalSetup]
    public void Setup()
    {
        _queue = new JobQueueService();
        for (var i = 0; i < 50; i++) _queue.Enqueue($@"C:\f{i}.bin", CompressionProfile.Archive);
    }
    [Benchmark] public int List_jobs() => _queue.List().Count;
    [Benchmark] public string Enqueue_one() => _queue.Enqueue(@"C:\t.bin", CompressionProfile.Balanced);
}
