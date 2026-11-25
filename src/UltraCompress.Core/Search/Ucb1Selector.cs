namespace UltraCompress.Core.Search;

/// <summary>
/// UCB1 index selection for heterogeneous pipeline arms (maximize reward).
/// </summary>
internal sealed class Ucb1Selector
{
    private readonly double _explorationConstant;
    private readonly Dictionary<string, ArmStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private int _totalPulls;

    public Ucb1Selector(double explorationConstant)
    {
        _explorationConstant = explorationConstant;
    }

    public void RegisterArms(IEnumerable<string> armIds)
    {
        foreach (var id in armIds)
        {
            _stats.TryAdd(id, new ArmStats());
        }
    }

    public void RecordPull(string armId, double reward)
    {
        if (!_stats.TryGetValue(armId, out var stats))
        {
            throw new InvalidOperationException($"Unknown arm '{armId}'.");
        }

        stats.Pulls++;
        stats.TotalReward += reward;
        _totalPulls++;
    }

    public string? SelectNext(IReadOnlySet<string> eligibleArmIds)
    {
        string? pick = null;
        var bestScore = double.NegativeInfinity;

        foreach (var id in eligibleArmIds)
        {
            if (!_stats.TryGetValue(id, out var stats))
            {
                continue;
            }

            var score = stats.Pulls == 0
                ? double.PositiveInfinity
                : (stats.MeanReward + _explorationConstant * Math.Sqrt(Math.Log(_totalPulls) / stats.Pulls));

            if (score > bestScore)
            {
                bestScore = score;
                pick = id;
            }
        }

        return pick;
    }

    public double UcbUpperBound(string armId)
    {
        if (!_stats.TryGetValue(armId, out var stats) || stats.Pulls == 0)
        {
            return double.PositiveInfinity;
        }

        return stats.MeanReward + _explorationConstant * Math.Sqrt(Math.Log(Math.Max(1, _totalPulls)) / stats.Pulls);
    }

    public int GetPullCount(string armId) =>
        _stats.TryGetValue(armId, out var stats) ? stats.Pulls : 0;

    public double GetMeanReward(string armId) =>
        _stats.TryGetValue(armId, out var stats) && stats.Pulls > 0 ? stats.MeanReward : 0;

    private sealed class ArmStats
    {
        public int Pulls { get; set; }

        public double TotalReward { get; set; }

        public double MeanReward => Pulls == 0 ? 0 : TotalReward / Pulls;
    }
}