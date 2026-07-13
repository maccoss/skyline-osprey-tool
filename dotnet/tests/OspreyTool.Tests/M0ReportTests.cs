using OspreyTool.Core;
using Xunit;

namespace OspreyTool.Tests;

public sealed class M0ReportTests
{
    private static PrecursorChromatograms Group(string file, string pep, int charge, double rtStart, double rtEnd)
    {
        var times = new[] { rtStart, (rtStart + rtEnd) / 2, rtEnd };
        return new PrecursorChromatograms
        {
            FileName = file,
            PeptideModifiedSequence = pep,
            PrecursorCharge = charge,
            Xics = new[]
            {
                new XicData { FragmentIndex = 0, RetentionTimes = times, Intensities = new[] { 1.0, 2, 1 }, FragmentIon = "precursor" },
                new XicData { FragmentIndex = 1, RetentionTimes = times, Intensities = new[] { 1.0, 3, 1 }, FragmentIon = "y7" },
            },
        };
    }

    private static LibraryEntry Entry(string pep, int charge, double? rt) => new()
    {
        PeptideSequence = pep,
        PeptideModifiedSequence = pep,
        PrecursorCharge = charge,
        PrecursorMz = 500,
        PredictedRetentionTime = rt,
    };

    [Fact]
    public void Build_reports_join_coverage_and_rt_window()
    {
        var chroms = new[]
        {
            Group("run1.raw", "PEPTIDER", 2, 7.0, 9.0),   // predicted RT 8.0 in window
            Group("run2.raw", "PEPTIDER", 2, 20.0, 22.0),  // predicted RT 8.0 OUT of window
            Group("run1.raw", "NOLIBK", 3, 1.0, 2.0),      // no library entry -> unmatched
        };
        var library = new[]
        {
            Entry("PEPTIDER", 2, 8.0),
            Entry("NEVERSEENK", 2, 5.0),  // in library, never extracted
        };

        var report = M0Report.Build(chroms, library);

        Assert.Equal(2, report.LibraryEntries);
        Assert.Equal(2, report.Replicates);
        Assert.Equal(3, report.PrecursorReplicateGroups);
        Assert.Equal(2, report.GroupsMatchedToLibrary);
        Assert.Equal(1, report.GroupsUnmatched);
        Assert.Equal(1, report.LibraryEntriesWithoutXics); // NEVERSEENK
        Assert.Equal(2, report.GroupsWithPredictedRt);
        Assert.Equal(1, report.GroupsPredictedRtInWindow); // only run1
        Assert.Equal(0, report.GroupsInconsistentGrid);
        Assert.Equal(0.5, report.RtInWindowFraction, 6);
        Assert.Equal(2, report.GroupsPerReplicate["run1.raw"]);
    }
}
