using OspreyTool.Scoring;
using Xunit;

namespace OspreyTool.Tests;

public sealed class SecondBestFdrTests
{
    [Fact]
    public void Cleanly_separated_targets_get_low_q()
    {
        // 50 targets all scoring above 50 nulls -> best achievable q is the (0+1)/nT pseudocount bound.
        var targets = Enumerable.Range(0, 50).Select(i => 1.0 + i * 0.01).ToList();
        var nulls = Enumerable.Range(0, 50).Select(i => 0.0 + i * 0.01).ToList();

        var q = SecondBestFdr.QValues(targets, nulls);

        Assert.Equal(50, q.Length);
        Assert.All(q, v => Assert.True(v <= 0.02 + 1e-9, $"q={v}")); // (0+1)/50 = 0.02
    }

    [Fact]
    public void Interleaved_nulls_penalize_the_low_targets_and_q_is_monotone()
    {
        // Three strong targets, then two nulls that beat the fourth (weak) target.
        var targets = new List<double> { 0.9, 0.85, 0.8, 0.4 };
        var nulls = new List<double> { 0.5, 0.45 };

        var q = SecondBestFdr.QValues(targets, nulls);

        // The weak target (below both nulls) carries the worst q.
        Assert.True(q[3] > q[0], $"q[weak]={q[3]:F3} should exceed q[strong]={q[0]:F3}");
        Assert.True(q[3] >= q[2]);
        // Every q is a valid probability.
        Assert.All(q, v => Assert.InRange(v, 0.0, 1.0));
    }

    [Fact]
    public void No_nulls_gives_the_pseudocount_floor()
    {
        var q = SecondBestFdr.QValues(new List<double> { 0.9, 0.8, 0.7 }, new List<double>());
        // (0+1)/3 for the lowest, cumulative-min propagates it up.
        Assert.All(q, v => Assert.Equal(1.0 / 3.0, v, 6));
    }
}
