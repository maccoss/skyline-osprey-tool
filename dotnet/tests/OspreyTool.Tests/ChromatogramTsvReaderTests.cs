using OspreyTool.Core;
using Xunit;

namespace OspreyTool.Tests;

public sealed class ChromatogramTsvReaderTests
{
    // Two precursors, one replicate: a 2+ precursor with a precursor row + one y7 fragment,
    // and a second precursor. Times/Intensities are comma arrays; TotalArea uses invariant
    // scientific notation exactly as Skyline exports it.
    private const string Sample =
        "FileName\tPeptideModifiedSequence\tPrecursorCharge\tProductMz\tFragmentIon\tProductCharge\tIsotopeLabelType\tTotalArea\tTimes\tIntensities\n" +
        "run1.raw\tPEPTIDER\t2\t500.25\tprecursor\t2\tlight\t6.400576E+07\t7.50,7.52,7.54\t10,20,15\n" +
        "run1.raw\tPEPTIDER\t2\t600.30\ty7\t1\tlight\t1.2E+06\t7.50,7.52,7.54\t5,9,6\n" +
        "run1.raw\tOTHERK\t3\t400.10\tprecursor\t3\tlight\t9.99E+05\t8.00,8.02\t100,110\n";

    [Fact]
    public void ReadGroups_parses_groups_arrays_and_invariant_numbers()
    {
        var groups = ChromatogramTsvReader.ReadGroups(new StringReader(Sample));

        Assert.Equal(2, groups.Count);

        var first = groups[0];
        Assert.Equal("run1.raw", first.FileName);
        Assert.Equal(new PrecursorKey("PEPTIDER", 2), first.Key);
        Assert.Equal(2, first.Xics.Count);

        // Fragment order preserved; index assigned by row order.
        Assert.Equal(0, first.Xics[0].FragmentIndex);
        Assert.True(first.Xics[0].IsPrecursor);
        Assert.Equal("y7", first.Xics[1].FragmentIon);
        Assert.Equal(1, first.Xics[1].FragmentIndex);

        // Comma arrays, equal length, correct values.
        Assert.Equal(new[] { 7.50, 7.52, 7.54 }, first.RetentionTimes);
        Assert.Equal(new[] { 10.0, 20.0, 15.0 }, first.Xics[0].Intensities);

        // Invariant scientific notation parsed correctly.
        Assert.Equal(6.400576e7, first.Xics[0].TotalArea, 3);

        // Shared, consistent RT grid across fragments.
        Assert.True(first.GridIsConsistent);
        Assert.Equal(7.50, first.RtStart, 6);
        Assert.Equal(7.54, first.RtEnd, 6);

        var second = groups[1];
        Assert.Equal(new PrecursorKey("OTHERK", 3), second.Key);
        Assert.Single(second.Xics);
    }

    [Fact]
    public void ReadGroups_rejects_wrong_column_count()
    {
        var bad =
            ChromatogramTsvReader.ExpectedHeader + "\n" +
            "run1.raw\tPEPTIDER\t2\n";
        Assert.Throws<FormatException>(() => ChromatogramTsvReader.ReadGroups(new StringReader(bad)));
    }
}
