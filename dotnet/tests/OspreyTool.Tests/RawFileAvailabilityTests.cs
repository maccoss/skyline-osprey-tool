using System.Xml;
using OspreyTool.Core;
using Xunit;

namespace OspreyTool.Tests;

public sealed class RawFileAvailabilityTests
{
    private const string Document =
        "<srm_settings><measured_results>" +
        "<replicate name=\"PRM-001\"><sample_file id=\"a\" file_path=\"D:\\data\\PRM-001.raw\" /></replicate>" +
        "<replicate name=\"PRM-002\"><sample_file id=\"b\" file_path=\"D:\\data\\PRM-002.raw|2|sampleB\" /></replicate>" +
        "</measured_results></srm_settings>";

    [Fact]
    public void ReadRawPaths_extracts_and_normalizes_sample_file_paths()
    {
        using var reader = XmlReader.Create(new StringReader(Document));
        var paths = RawFileAvailability.ReadRawPaths(reader);

        Assert.Equal(2, paths.Count);
        Assert.Equal("D:\\data\\PRM-001.raw", paths[0]);
        // Multi-sample selector after '|' is stripped to the on-disk path.
        Assert.Equal("D:\\data\\PRM-002.raw", paths[1]);
    }

    [Fact]
    public void Evaluate_partitions_accessible_and_missing()
    {
        var paths = new[] { "A.raw", "B.raw", "C.raw" };
        var present = new HashSet<string> { "A.raw", "C.raw" };

        var availability = RawFileAvailability.Evaluate(paths, present.Contains);

        Assert.False(availability.AllAccessible);
        Assert.True(availability.AnyMissing);
        Assert.Equal(new[] { "A.raw", "C.raw" }, availability.AccessiblePaths);
        Assert.Equal(new[] { "B.raw" }, availability.MissingPaths);
    }

    [Fact]
    public void AllAccessible_true_only_when_all_present_and_nonempty()
    {
        var all = RawFileAvailability.Evaluate(new[] { "A.raw", "B.raw" }, _ => true);
        Assert.True(all.AllAccessible);

        var none = RawFileAvailability.Evaluate(Array.Empty<string>(), _ => true);
        Assert.False(none.AllAccessible); // no raw referenced is not "all accessible"
    }
}
