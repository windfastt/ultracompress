namespace UltraCompress.Benchmarks.Engine;

/// <summary>
/// Deterministic UCB1 multi-armed bandit selector for pilot pipeline screening in the Tiny profile.
/// </summary>
internal sealed class Ucb1Bandit
{
    private readonly double _explorationConstant;
    private readonly List<string> _arms = new();
    private readonly Dictionary<string, (int Pulls, double SumReward)> _stats = new(StringComparer.Ordinal);
    private int _totalPulls;

    public Ucb1Bandit(double explorationConstant = 0.35)
    {
        _explorationConstant = explorationConstant;
    }

    public void RegisterArms(IEnumerable<string> armIds)
    {
        foreach (var id in armIds)
        {
            if (_stats.TryAdd(id, (0, 0.0)))
            {
                _arms.Add(id);
            }
        }
    }

    public int GetPullCount(string armId) => _stats.TryGetValue(armId, out var s) ? s.Pulls : 0;

    public double GetMeanReward(string armId) =>
        _stats.TryGetValue(armId, out var s) && s.Pulls > 0 ? s.SumReward / s.Pulls : 0.0;

    public string? SelectNext(IReadOnlySet<string> eligible)
    {
        foreach (var id in _arms)
        {
            if (eligible.Contains(id) && _stats[id].Pulls == 0)
            {
                return id;
            }
        }

        string? bestId = null;
        double bestScore = double.NegativeInfinity;
        double logN = Math.Log(Math.Max(1, _totalPulls));

        foreach (var id in _arms)
        {
            if (!eligible.Contains(id)) continue;
            var (pulls, sum) = _stats[id];
            double mean = sum / pulls;
            double score = mean + (_explorationConstant * Math.Sqrt((2.0 * logN) / pulls));
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        return bestId;
    }

    public void RecordPull(string armId, double reward)
    {
        var (pulls, sum) = _stats[armId];
        _stats[armId] = (pulls + 1, sum + reward);
        _totalPulls++;
    }
}
