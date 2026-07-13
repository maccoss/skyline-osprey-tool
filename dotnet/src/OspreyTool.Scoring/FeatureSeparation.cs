namespace OspreyTool.Scoring;

/// <summary>
/// How well one feature separates targets from decoys, as the rank-based AUC (the probability a
/// random target scores above a random decoy - Mann-Whitney U / nT*nD). 0.5 = no separation,
/// 1.0 = perfect. This is the M0 science check: the co-elution and median-polish features should
/// score well above 0.5 for decoy-XIC decoys.
/// </summary>
public sealed class FeatureSeparation
{
    public required int FeatureIndex { get; init; }

    public required string FeatureName { get; init; }

    public required double TargetMean { get; init; }

    public required double DecoyMean { get; init; }

    /// <summary>Probability a random target outscores a random decoy (rank AUC).</summary>
    public required double Auc { get; init; }

    public int TargetCount { get; init; }

    public int DecoyCount { get; init; }

    public static FeatureSeparation Compute(
        int featureIndex,
        string featureName,
        IReadOnlyList<double> targetValues,
        IReadOnlyList<double> decoyValues)
    {
        return new FeatureSeparation
        {
            FeatureIndex = featureIndex,
            FeatureName = featureName,
            TargetMean = Mean(targetValues),
            DecoyMean = Mean(decoyValues),
            Auc = RankAuc(targetValues, decoyValues),
            TargetCount = targetValues.Count,
            DecoyCount = decoyValues.Count,
        };
    }

    private static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }
        double sum = 0;
        foreach (var v in values)
        {
            sum += v;
        }
        return sum / values.Count;
    }

    /// <summary>Mann-Whitney AUC via average ranks over the pooled sample (ties count as 0.5).</summary>
    private static double RankAuc(IReadOnlyList<double> targets, IReadOnlyList<double> decoys)
    {
        var nT = targets.Count;
        var nD = decoys.Count;
        if (nT == 0 || nD == 0)
        {
            return double.NaN;
        }

        var pooled = new (double value, bool isTarget)[nT + nD];
        var k = 0;
        foreach (var v in targets)
        {
            pooled[k++] = (v, true);
        }
        foreach (var v in decoys)
        {
            pooled[k++] = (v, false);
        }
        Array.Sort(pooled, (a, b) => a.value.CompareTo(b.value));

        // Average ranks (1-based), summed over targets.
        double targetRankSum = 0;
        var i = 0;
        while (i < pooled.Length)
        {
            var j = i;
            while (j < pooled.Length && pooled[j].value == pooled[i].value)
            {
                j++;
            }
            var averageRank = (i + 1 + j) / 2.0; // mean of ranks (i+1)..j
            for (var t = i; t < j; t++)
            {
                if (pooled[t].isTarget)
                {
                    targetRankSum += averageRank;
                }
            }
            i = j;
        }

        var u = targetRankSum - nT * (nT + 1) / 2.0;
        return u / ((double)nT * nD);
    }
}
