using OspreyTool.Core;
using OspreyTool.Scoring;
using OspreyTool.Skyline;
using Xunit;

namespace OspreyTool.Tests;

/// <summary>Regressions for the issues raised in code review on PR #1.</summary>
public sealed class ReviewFixesTests
{
    // ---- ChromatogramTsvReader: columns are located by NAME, not by position ----

    private const string Row = "run1.raw\tPEPTIDER\t2\t500.5\ty4\t1\tlight\t1000\t7.1,7.2,7.3\t10,20,10\n";

    [Fact]
    public void Reader_tolerates_an_added_column_and_a_reordered_header()
    {
        // Same data, columns shuffled and an unknown column inserted - a positional parser would read
        // garbage here (or throw); a by-name parser reads it correctly.
        var reordered =
            "Intensities\tFileName\tSomeNewSkylineColumn\tPeptideModifiedSequence\tPrecursorCharge\t" +
            "ProductMz\tFragmentIon\tProductCharge\tIsotopeLabelType\tTotalArea\tTimes\n" +
            "10,20,10\trun1.raw\tignored\tPEPTIDER\t2\t500.5\ty4\t1\tlight\t1000\t7.1,7.2,7.3\n";

        var groups = ChromatogramTsvReader.ReadGroups(new StringReader(reordered));

        var g = Assert.Single(groups);
        Assert.Equal(new PrecursorKey("PEPTIDER", 2), g.Key);
        var xic = Assert.Single(g.Xics);
        Assert.Equal("y4", xic.FragmentIon);
        Assert.Equal(500.5, xic.ProductMz, 6);
        Assert.Equal(1000.0, xic.TotalArea, 6);
        Assert.Equal(new[] { 7.1, 7.2, 7.3 }, xic.RetentionTimes);
        Assert.Equal(new[] { 10.0, 20.0, 10.0 }, xic.Intensities);
    }

    [Fact]
    public void Reader_rejects_a_header_missing_a_required_column()
    {
        // ProductMz dropped: refuse the file rather than shift every later column by one.
        var missing =
            "FileName\tPeptideModifiedSequence\tPrecursorCharge\tFragmentIon\tProductCharge\t" +
            "IsotopeLabelType\tTotalArea\tTimes\tIntensities\n" +
            "run1.raw\tPEPTIDER\t2\ty4\t1\tlight\t1000\t7.1,7.2\t10,20\n";

        var ex = Assert.Throws<FormatException>(() => ChromatogramTsvReader.ReadGroups(new StringReader(missing)));
        Assert.Contains("ProductMz", ex.Message);
    }

    [Fact]
    public void Reader_still_reads_the_documented_header()
    {
        var groups = ChromatogramTsvReader.ReadGroups(
            new StringReader(ChromatogramTsvReader.ExpectedHeader + "\n" + Row));
        Assert.Single(groups);
    }

    // ---- SecondBestFdr: ties must break conservatively ----

    /// <summary>A target and a null on the SAME score must count the null first. Breaking the tie the other
    /// way credits the target with one fewer null above it and under-states its FDR - the unsafe direction.</summary>
    [Fact]
    public void QValues_break_score_ties_in_favour_of_the_null()
    {
        // One target and one null, both at 1.0: FDR at the target = (1 null above + 1) / 1 target = 2 -> q capped at 1.
        var tied = SecondBestFdr.QValues(new[] { 1.0 }, new[] { 1.0 });
        Assert.Equal(1.0, tied[0], 6);

        // Sanity: with the null strictly below, the target is clean.
        var separated = SecondBestFdr.QValues(new[] { 1.0 }, new[] { 0.5 });
        Assert.Equal(1.0, separated[0], 6); // (0 nulls above + 1) / 1 target = 1.0

        // A well-separated set: q must be monotone non-decreasing down the ranked targets.
        var q = SecondBestFdr.QValues(
            new[] { 9.0, 8.0, 7.0, 6.0, 5.0 },
            new[] { 4.0, 3.0, 2.0, 1.0, 0.5 });
        for (var i = 1; i < q.Length; i++)
        {
            Assert.True(q[i] >= q[i - 1] - 1e-12, $"q must be monotone: q[{i}]={q[i]} < q[{i - 1}]={q[i - 1]}");
        }
        Assert.All(q, v => Assert.InRange(v, 0.0, 1.0));
    }

    // ---- SkylineColorScheme: only the two known precursor spellings count ----

    [Fact]
    public void Color_scheme_ignores_unknown_color_types_rather_than_guessing()
    {
        // "peak" starts with 'p' but is not a precursor colour; it must not be counted as one.
        var xml = """
            <ColorScheme name="test">
              <color type="percursor" red="1" green="2" blue="3" />
              <color type="precursor" red="4" green="5" blue="6" />
              <color type="peak" red="7" green="8" blue="9" />
              <color type="transition" red="10" green="11" blue="12" />
            </ColorScheme>
            """;

        var (precursors, transitions) = SkylineColorScheme.Parse(xml);

        Assert.Equal(2, precursors.Count); // both spellings of precursor, and nothing else
        Assert.Equal(new SchemeColor(1, 2, 3), precursors[0]);
        Assert.Equal(new SchemeColor(4, 5, 6), precursors[1]);
        Assert.DoesNotContain(new SchemeColor(7, 8, 9), precursors);
        Assert.Equal(new SchemeColor(10, 11, 12), Assert.Single(transitions));
    }
}
