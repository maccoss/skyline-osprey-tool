namespace OspreyTool.Scoring;

/// <summary>
/// Target-decoy q-values using **second-best peaks** as the null - the mProphet approach for target-only
/// data (Skyline's <c>--reintegrate-model-second-best</c>). The best peak in each target's chromatogram is
/// the target example and the runner-up (second-best) peak is the decoy. A higher score is more
/// target-like (here, fragment co-elution). Estimator: at a score threshold, FDR = (nNull + 1) / nTarget;
/// q-value is the cumulative minimum FDR from the worst score up, so it is monotone.
/// </summary>
public static class SecondBestFdr
{
    /// <summary>
    /// Returns a q-value per target, aligned to <paramref name="targetScores"/>. <paramref name="nullScores"/>
    /// are the second-best-peak scores (need not be the same length).
    /// </summary>
    public static double[] QValues(IReadOnlyList<double> targetScores, IReadOnlyList<double> nullScores)
    {
        var nT = targetScores.Count;
        var q = new double[nT];
        if (nT == 0)
        {
            return q;
        }

        // Pool targets + nulls, sorted by score descending; targets remember their input index.
        var pooled = new List<(double score, bool isTarget, int idx)>(nT + nullScores.Count);
        for (var i = 0; i < nT; i++)
        {
            pooled.Add((targetScores[i], true, i));
        }
        foreach (var s in nullScores)
        {
            pooled.Add((s, false, -1));
        }
        // Descending by score, and at an EQUAL score put nulls before targets. Without the tie-break the
        // sort is unstable, so a target and a null on the same score could fall either way: counting the
        // target first would credit it with one fewer null above it and under-estimate its FDR. Ordering
        // nulls first makes ties break conservatively (the estimate can only be too high, never too low).
        pooled.Sort((a, b) =>
        {
            var byScore = b.score.CompareTo(a.score);
            return byScore != 0 ? byScore : a.isTarget.CompareTo(b.isTarget); // false (null) sorts first
        });

        // Forward pass: FDR at each target's rank = (decoysSoFar + 1) / targetsSoFar.
        var targetOrder = new (int idx, double fdr)[nT];
        var cntT = 0;
        var cntD = 0;
        var ti = 0;
        foreach (var e in pooled)
        {
            if (e.isTarget)
            {
                cntT++;
                targetOrder[ti++] = (e.idx, (double)(cntD + 1) / cntT);
            }
            else
            {
                cntD++;
            }
        }

        // Backward cumulative minimum -> monotone q-value.
        var running = double.MaxValue;
        for (var k = nT - 1; k >= 0; k--)
        {
            running = Math.Min(running, targetOrder[k].fdr);
            q[targetOrder[k].idx] = Math.Min(1.0, running);
        }
        return q;
    }
}
