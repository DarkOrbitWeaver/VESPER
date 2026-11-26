using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Intelligence;

/// <summary>
/// Calibrated online Bayesian Bandit (Beta-Bernoulli UCB/Thompson posterior with exponential half-life decay)
/// combined with L2-regularized sign-constrained online logistic weight adaptation over verified outcomes.
/// Learns route reliability, CDN header/cookie policies, and source-association weights locally without cloud telemetry.
/// </summary>
public sealed class OnlineLearner
{
    private const int MaxArms = 512;
    private const double HalfLifeHours = 168.0; // 7-day exponential half-life so site changes heal automatically
    private const double MaxEffectiveObservations = 32.0;
    private const double LearningRate = 0.06;
    private const double L2Regularization = 0.02;

    public sealed record ArmPosterior(double Alpha, double Beta, double AvgThroughputBps, long UpdatedUnixMs)
    {
        public double Mean => Alpha / (Alpha + Beta);
        public double Variance => (Alpha * Beta) / (Math.Pow(Alpha + Beta, 2) * (Alpha + Beta + 1));
        public double UcbScore(double exploration = 1.25) => Mean + exploration * Math.Sqrt(Variance);
    }

    private sealed class PersistedState
    {
        public string Salt { get; set; } = Guid.NewGuid().ToString("N");
        public Dictionary<string, ArmPosterior> Arms { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> SourceWeightDeltas { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly object _gate = new();
    private readonly string? _persistencePath;
    private PersistedState _state = new();

    public OnlineLearner(string? persistencePath = null)
    {
        _persistencePath = persistencePath;
        LoadFromDisk();
    }

    public void RecordRouteOutcome(string domainOrProvider, string route, bool succeeded, double bytesPerSecond = 0, DateTimeOffset? now = null)
    {
        long ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        lock (_gate)
        {
            string key = HashKey(domainOrProvider, route);
            ArmPosterior decayed = GetDecayedLocked(key, ts);
            double alpha = decayed.Alpha + (succeeded ? 1.0 : 0.0);
            double beta = decayed.Beta + (succeeded ? 0.0 : 1.0);
            double total = alpha + beta;
            if (total > MaxEffectiveObservations)
            {
                double scale = MaxEffectiveObservations / total;
                alpha = Math.Max(1.0, alpha * scale);
                beta = Math.Max(1.0, beta * scale);
            }
            double speed = succeeded && bytesPerSecond > 0
                ? (decayed.AvgThroughputBps > 0 ? decayed.AvgThroughputBps * 0.65 + bytesPerSecond * 0.35 : bytesPerSecond)
                : decayed.AvgThroughputBps;

            _state.Arms[key] = new ArmPosterior(alpha, beta, speed, ts);
            TrimArmsLocked();
            SaveToDiskLocked();
        }
    }

    public string? SelectBestRoute(string domainOrProvider, IReadOnlyList<string> candidateRoutes, DateTimeOffset? now = null)
    {
        if (candidateRoutes.Count == 0) return null;
        if (candidateRoutes.Count == 1) return candidateRoutes[0];
        long ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        lock (_gate)
        {
            string? bestRoute = null;
            double bestScore = double.NegativeInfinity;
            foreach (string route in candidateRoutes)
            {
                string key = HashKey(domainOrProvider, route);
                ArmPosterior arm = GetDecayedLocked(key, ts);
                double throughputBonus = arm.AvgThroughputBps > 0 ? Math.Min(0.12, Math.Log10(1 + arm.AvgThroughputBps / 1_000_000.0) * 0.06) : 0;
                double score = arm.UcbScore() + throughputBonus;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoute = route;
                }
            }
            return bestRoute;
        }
    }

    /// <summary>Pairwise online logistic update from a verified source outcome vs rejected competitors.</summary>
    public void UpdateSourceWeights(RankingModel baseModel, RankingCandidate verifiedWinner, IReadOnlyList<RankingCandidate> competitors)
    {
        if (competitors.Count == 0) return;
        lock (_gate)
        {
            RankingModel active = baseModel.WithOnlineAdjustments(_state.SourceWeightDeltas);
            foreach (RankingCandidate other in competitors)
            {
                if (other.Id == verifiedWinner.Id) continue;
                double diffDot = 0;
                foreach (string feature in FeatureSchema.SourceAssociation.Features)
                {
                    double w = active.Weights[feature];
                    double xWin = Val(verifiedWinner, feature);
                    double xLose = Val(other, feature);
                    diffDot += w * (xWin - xLose);
                }
                double prob = 1.0 / (1.0 + Math.Exp(-Math.Clamp(diffDot, -20, 20)));
                double err = 1.0 - prob;
                foreach (string feature in FeatureSchema.SourceAssociation.Features)
                {
                    double diff = Val(verifiedWinner, feature) - Val(other, feature);
                    if (Math.Abs(diff) < 1e-9) continue;
                    double currentDelta = _state.SourceWeightDeltas.GetValueOrDefault(feature, 0.0);
                    double nextDelta = currentDelta + LearningRate * (err * diff - L2Regularization * currentDelta);
                    nextDelta = Math.Clamp(nextDelta, -4.0, 4.0);
                    double effective = baseModel.Weights[feature] + nextDelta;
                    if (FeatureSchema.SourceAssociation.Negative.Contains(feature) && effective > 0)
                        nextDelta = -baseModel.Weights[feature];
                    else if (!FeatureSchema.SourceAssociation.Negative.Contains(feature) && effective < 0)
                        nextDelta = -baseModel.Weights[feature];
                    _state.SourceWeightDeltas[feature] = Math.Round(nextDelta, 5);
                }
            }
            SaveToDiskLocked();
        }
    }

    public IReadOnlyDictionary<string, double> GetSourceWeightDeltas()
    {
        lock (_gate) return new Dictionary<string, double>(_state.SourceWeightDeltas, StringComparer.Ordinal);
    }

    private static double Val(RankingCandidate c, string feature) =>
        c.Facts.TryGetValue(feature, out double? v) && v is { } d && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;

    private ArmPosterior GetDecayedLocked(string key, long nowMs)
    {
        if (!_state.Arms.TryGetValue(key, out ArmPosterior? prior))
            return new ArmPosterior(1.0, 1.0, 0, nowMs); // Uniform Beta(1,1) prior
        double elapsedHours = Math.Max(0, (nowMs - prior.UpdatedUnixMs) / 3_600_000.0);
        double decay = Math.Pow(0.5, elapsedHours / HalfLifeHours);
        double alpha = 1.0 + (prior.Alpha - 1.0) * decay;
        double beta = 1.0 + (prior.Beta - 1.0) * decay;
        return new ArmPosterior(alpha, beta, prior.AvgThroughputBps * decay, nowMs);
    }

    private string HashKey(string scope, string arm)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{_state.Salt}\n{scope.ToLowerInvariant().Trim()}\n{arm}");
        return Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 12)).ToLowerInvariant();
    }

    private void TrimArmsLocked()
    {
        if (_state.Arms.Count <= MaxArms) return;
        foreach (string oldest in _state.Arms.OrderBy(kv => kv.Value.UpdatedUnixMs).Take(_state.Arms.Count - MaxArms).Select(kv => kv.Key).ToArray())
            _state.Arms.Remove(oldest);
    }

    private void LoadFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_persistencePath) || !File.Exists(_persistencePath)) return;
        try
        {
            FileInfo info = new(_persistencePath);
            if (info.Length is <= 0 or > 1024 * 1024) return;
            var loaded = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_persistencePath, Encoding.UTF8));
            if (loaded is { Salt.Length: >= 8 } && loaded.Arms is not null && loaded.SourceWeightDeltas is not null)
                _state = loaded;
        }
        catch { }
    }

    private void SaveToDiskLocked()
    {
        if (string.IsNullOrWhiteSpace(_persistencePath)) return;
        try
        {
            string? dir = Path.GetDirectoryName(_persistencePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _persistencePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state), Encoding.UTF8);
            File.Move(tmp, _persistencePath, overwrite: true);
        }
        catch { }
    }
}
