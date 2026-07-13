using OspreyTool.Scoring;
using Xunit;

namespace OspreyTool.Tests;

public sealed class FeatureSelectionTests
{
    [Fact]
    public void No_raw_uses_only_the_12_and_warns_about_spectrum_scores()
    {
        var selection = FeatureSelection.Resolve(observedSpectraAvailable: false);

        Assert.Equal(OspreyFeatureFamilies.XicRtMedianPolish.OrderBy(i => i), selection.FeatureIndices);
        Assert.Equal(12, selection.FeatureIndices.Count);

        // The user must be told which scores were dropped when the raw is missing.
        Assert.Contains(selection.Warnings, w => w.Contains("apex-spectrum match") && w.Contains("WARNING"));
        Assert.Contains(selection.Warnings, w => w.Contains("xcorr"));
        Assert.Contains(selection.Warnings, w => w.Contains("MS1"));
        // Names of the dropped features are surfaced, not just indices.
        Assert.Contains(selection.Warnings, w => w.Contains("consecutive_ions"));
    }

    [Fact]
    public void Spectra_present_reaches_19_features()
    {
        var selection = FeatureSelection.Resolve(observedSpectraAvailable: true);

        Assert.Equal(19, selection.FeatureIndices.Count); // 12 + 4 apex-match + 3 window/xcorr; MS1 (2) out
        foreach (var i in OspreyFeatureFamilies.ApexSpectrumMatch.Concat(OspreyFeatureFamilies.WindowXcorr))
        {
            Assert.Contains(i, selection.FeatureIndices);
        }
        // MS1 remains out on unit-resolution PRM and is warned.
        Assert.DoesNotContain(13, selection.FeatureIndices);
        Assert.Contains(selection.Warnings, w => w.Contains("MS1"));
    }

    [Fact]
    public void Spectra_present_but_xcorr_staged_off_reaches_16()
    {
        var selection = FeatureSelection.Resolve(observedSpectraAvailable: true, includeWindowXcorr: false);

        Assert.Equal(16, selection.FeatureIndices.Count); // 12 + 4 apex-match only
        Assert.DoesNotContain(6, selection.FeatureIndices);
        Assert.Contains(selection.Warnings, w => w.Contains("xcorr / SG"));
    }
}
