using OspreyTool.Core;
using OspreyTool.Scoring;
using OspreyTool.Scoring.Ranking;
using Xunit;

namespace OspreyTool.Tests;

/// <summary>
/// Osprey ships one frozen peak-pick model per platform and they are NOT interchangeable, so which one
/// applies has to follow the instrument that extracted the chromatograms. The split is the fragment-tolerance
/// unit: a fixed m/z window (LIT, or a triple quad matching on mz_match_tolerance) -> Stellar weights;
/// a ppm / resolving-power analyzer -> Astral weights.
/// </summary>
public sealed class ScoringSetupTests
{
    private static string WriteDoc(string transitionSettingsXml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ospreytool-{Guid.NewGuid():N}.sky");
        File.WriteAllText(path,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<srm_settings format_version=\"23.1\">\n" +
            $"  <settings_summary name=\"Default\">\n    <transition_settings>\n{transitionSettingsXml}\n" +
            "    </transition_settings>\n  </settings_summary>\n</srm_settings>\n");
        return path;
    }

    private static PickLdaModel ModelFor(string transitionSettingsXml)
    {
        var path = WriteDoc(transitionSettingsXml);
        try
        {
            var setup = ScoringSetupFactory.Resolve(path, null, "lda");
            return Assert.IsType<PickLdaRankModel>(setup.Ranker).Model;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Thermo Stellar PRM: ion-trap product analyzer, 0.5 m/z extraction width.</summary>
    [Fact]
    public void Ion_trap_gets_the_unit_resolution_weights()
    {
        Assert.Same(PickLdaModel.Stellar, ModelFor(
            "      <transition_instrument mz_match_tolerance=\"0.055\" />\n" +
            "      <transition_full_scan acquisition_method=\"PRM\" product_mass_analyzer=\"qit\" " +
            "product_res=\"0.5\" precursor_mass_analyzer=\"qit\" precursor_res=\"0.7\" />"));
    }

    /// <summary>A triple quad (SRM): no full-scan product analyzer at all, so matching is the instrument's
    /// m/z tolerance - unit resolution, exactly like the ion trap.</summary>
    [Fact]
    public void Triple_quad_matching_on_mz_gets_the_unit_resolution_weights()
    {
        Assert.Same(PickLdaModel.Stellar, ModelFor(
            "      <transition_instrument mz_match_tolerance=\"0.055\" />"));
    }

    [Theory]
    [InlineData("orbitrap")]
    [InlineData("tof")]
    [InlineData("ft_icr")]
    [InlineData("centroided")]
    public void High_resolution_analyzers_get_the_hram_weights(string analyzer)
    {
        Assert.Same(PickLdaModel.Astral, ModelFor(
            "      <transition_instrument mz_match_tolerance=\"0.055\" />\n" +
            $"      <transition_full_scan acquisition_method=\"PRM\" product_mass_analyzer=\"{analyzer}\" " +
            "product_res=\"30000\" precursor_mass_analyzer=\"orbitrap\" precursor_res=\"60000\" />"));
    }

    [Fact]
    public void Explicit_resolution_override_wins_over_the_document()
    {
        var path = WriteDoc("      <transition_full_scan product_mass_analyzer=\"qit\" product_res=\"0.5\" />");
        try
        {
            var forced = ScoringSetupFactory.Resolve(path, ScoringSetupFactory.Hram, "lda");
            Assert.Same(PickLdaModel.Astral, Assert.IsType<PickLdaRankModel>(forced.Ranker).Model);
            Assert.Contains("--resolution hram", forced.ResolutionSource);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>With nothing to go on the tool assumes its documented target (unit-resolution PRM) - but the
    /// source string must say so, so an assumption is never silent.</summary>
    [Fact]
    public void Without_a_document_the_assumption_is_stated()
    {
        var setup = ScoringSetupFactory.Resolve(null, null, "lda");

        Assert.Same(PickLdaModel.Stellar, Assert.IsType<PickLdaRankModel>(setup.Ranker).Model);
        Assert.Contains("assumed unit resolution", setup.ResolutionSource);
        Assert.Contains("--sky", setup.ResolutionSource);
    }

    [Fact]
    public void Product_ranker_is_selectable_and_resolution_independent()
    {
        var unit = ScoringSetupFactory.Resolve(null, ScoringSetupFactory.UnitResolution, "product");
        var hram = ScoringSetupFactory.Resolve(null, ScoringSetupFactory.Hram, "product");

        Assert.Same(ProductRankModel.Instance, unit.Ranker);
        Assert.Same(ProductRankModel.Instance, hram.Ranker);
    }

    /// <summary>The document also drives the fragment tolerance, and that must stay consistent with the
    /// resolution the pick model was chosen for.</summary>
    [Fact]
    public void Document_resolution_also_drives_the_osprey_tolerances()
    {
        var path = WriteDoc(
            "      <transition_full_scan product_mass_analyzer=\"qit\" product_res=\"0.5\" " +
            "precursor_mass_analyzer=\"qit\" precursor_res=\"0.7\" />");
        try
        {
            var setup = ScoringSetupFactory.Resolve(path, null, "lda");
            Assert.Equal(pwiz.Osprey.Core.ResolutionMode.UnitResolution, setup.Osprey.ResolutionMode);
            Assert.Contains("qit", setup.ResolutionSource);
            Assert.Contains("unit resolution", setup.ResolutionSource);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
