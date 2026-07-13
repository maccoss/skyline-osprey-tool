using OspreyTool.Core;
using OspreyTool.Scoring;
using Xunit;

namespace OspreyTool.Tests;

public sealed class ReconciliationPipelineTests
{
    // One Gaussian fragment; sigmaSq controls the natural peak WIDTH so different replicates yield
    // different CWT boundaries (which reconciliation should then normalize to the consensus).
    private static XicData Frag(int idx, double mz, double rel, int apex, double sigmaSq, int n)
    {
        var t = new double[n];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            t[i] = 7.0 + i * 0.02;
            var d = i - apex;
            y[i] = rel * 1000.0 * System.Math.Exp(-(d * d) / sigmaSq);
        }
        return new XicData { FragmentIndex = idx, RetentionTimes = t, Intensities = y, FragmentIon = $"y{idx}", ProductMz = mz };
    }

    private static PrecursorChromatograms Group(string file, string seq, int apex, double sigmaSq, int n = 100) => new()
    {
        FileName = file,
        PeptideModifiedSequence = seq,
        PrecursorCharge = 2,
        Xics = new List<XicData>
        {
            Frag(0, 100.0, 1.0, apex, sigmaSq, n),
            Frag(1, 200.0, 0.6, apex, sigmaSq, n),
            Frag(2, 300.0, 0.3, apex, sigmaSq, n),
        },
    };

    /// <summary>22 peptides (>= the RT-calibration minimum of 20) x 3 replicates. The three replicates give
    /// the SAME peptide peaks of different natural width (sigmaSq 6 / 3 / 12), so per-run CWT alone yields
    /// ragged boundaries and reconciliation has something to normalize.</summary>
    private static (List<PrecursorChromatograms> Groups, Dictionary<PrecursorKey, double> Rts) BuildExperiment()
    {
        var replicates = new[] { ("repA", 6.0), ("repB", 3.0), ("repC", 12.0) };
        var groups = new List<PrecursorChromatograms>();
        var rts = new Dictionary<PrecursorKey, double>();
        for (var p = 0; p < 22; p++)
        {
            var seq = $"PEPTIDEK{(char)('A' + p)}";
            var apex = 10 + p * 3; // 10..73: distinct RTs, all interior to the 100-scan grid
            rts[new PrecursorKey(seq, 2)] = 7.0 + apex * 0.02;
            foreach (var (rep, sq) in replicates)
            {
                groups.Add(Group(rep, seq, apex, sq));
            }
        }
        return (groups, rts);
    }

    private static ReconcileSummary Run(ReconcileConfig config)
    {
        var (groups, rts) = BuildExperiment();
        return ReconciliationPipeline.Run(groups, rts,
            new HashSet<PrecursorKey>(), new Dictionary<PrecursorKey, double>(), config);
    }

    // Widths of a peptide's picked peaks across the replicates, per peptide.
    private static List<List<double>> WidthsPerPeptide(ReconcileSummary s) =>
        s.Rows.GroupBy(r => (r.PeptideModifiedSequence, r.PrecursorCharge))
            .Select(g => g.Where(r => !double.IsNaN(r.MinStartTime) && !double.IsNaN(r.MaxEndTime))
                .Select(r => r.MaxEndTime - r.MinStartTime).ToList())
            .Where(w => w.Count >= 2)
            .ToList();

    [Fact]
    public void Reconciliation_normalizes_peak_width_across_replicates()
    {
        var s = Run(new ReconcileConfig { UseFdr = false, AllTargets = true, RtTolerance = 0.0, RtSigma = 0.3 });

        Assert.Equal(22 * 3, s.Rows.Count);
        Assert.True(s.TargetsScored > 0, "targets should have been scored");
        Assert.True(s.ConsensusPeptides > 0, "inter-run reconciliation should have built a consensus");

        var widths = WidthsPerPeptide(s);
        Assert.NotEmpty(widths);
        Assert.All(widths, w => Assert.True(w.Max() - w.Min() < 1e-6,
            $"widths not uniform after reconciliation: [{w.Min():F3}, {w.Max():F3}]"));
    }

    [Fact]
    public void Without_reconciliation_the_per_run_widths_stay_ragged()
    {
        // Control for the test above: the SAME input, reconciliation off. Proves the uniform widths there
        // are produced by reconciliation and not by the synthetic peaks happening to be identical.
        var s = Run(new ReconcileConfig
        {
            UseFdr = false, AllTargets = true, RtTolerance = 0.0, RtSigma = 0.3,
            InterRunReconcile = false, ChargeConsensus = false,
        });

        Assert.Equal(22 * 3, s.Rows.Count);
        Assert.Equal(0, s.ConsensusPeptides);
        Assert.All(s.Rows, r => Assert.Equal("keep", r.Action)); // nothing moved
        Assert.Equal(0, s.UseCwtMoves);
        Assert.Equal(0, s.ForcedIntegrations);

        var ragged = WidthsPerPeptide(s).Count(w => w.Max() - w.Min() > 1e-6);
        Assert.True(ragged > 0, "per-run CWT should give differently-WIDE peaks for the three replicate shapes");
    }
}
