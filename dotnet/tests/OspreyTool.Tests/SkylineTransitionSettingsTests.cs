using System.Xml;
using OspreyTool.Core;
using Xunit;

namespace OspreyTool.Tests;

public sealed class SkylineTransitionSettingsTests
{
    // The transition_settings block verbatim from the real Stellar PRM test document, wrapped in a
    // minimal srm_settings so the reader sees the same structure (and peptide data after it to prove
    // the reader stops at </transition_settings>).
    private const string Document =
        "<srm_settings><settings_summary><transition_settings>" +
        "<transition_prediction precursor_mass_type=\"Monoisotopic\" fragment_mass_type=\"Monoisotopic\" />" +
        "<transition_filter precursor_charges=\"2,3\" product_charges=\"1,2,3\" fragment_types=\"y,b,p\" />" +
        "<transition_libraries ion_match_tolerance=\"20\" ion_match_tolerance_unit=\"ppm\" min_ion_count=\"4\" ion_count=\"6\" />" +
        "<transition_integration integrate_all=\"true\" />" +
        "<transition_instrument min_mz=\"200\" max_mz=\"1960\" mz_match_tolerance=\"0.055\" />" +
        "<transition_full_scan acquisition_method=\"PRM\" product_mass_analyzer=\"qit\" product_res=\"0.5\" " +
        "precursor_mass_analyzer=\"qit\" precursor_res=\"0.7\" retention_time_filter_type=\"scheduling_windows\" />" +
        "</transition_settings></settings_summary>" +
        "<peptide_list><peptide sequence=\"PEPTIDER\" /></peptide_list></srm_settings>";

    [Fact]
    public void Reads_unit_resolution_tolerances_from_transition_settings()
    {
        using var reader = XmlReader.Create(new StringReader(Document));
        var s = SkylineTransitionSettings.Read(reader);

        Assert.Equal("PRM", s.AcquisitionMethod);
        Assert.Equal("qit", s.ProductMassAnalyzer);
        Assert.Equal("qit", s.PrecursorMassAnalyzer);
        Assert.Equal(0.5, s.ProductRes, 6);
        Assert.Equal(0.7, s.PrecursorRes, 6);
        Assert.Equal(0.055, s.MzMatchTolerance, 6);

        Assert.True(s.IsUnitResolution);
        Assert.Equal(0.5, s.EffectiveProductTolerance, 6);
        Assert.Equal(0.7, s.EffectivePrecursorTolerance, 6);
    }

    [Fact]
    public void Falls_back_to_instrument_tolerance_when_no_full_scan_res()
    {
        var doc =
            "<srm_settings><transition_settings>" +
            "<transition_instrument mz_match_tolerance=\"0.055\" />" +
            "<transition_full_scan acquisition_method=\"PRM\" product_mass_analyzer=\"qit\" />" +
            "</transition_settings></srm_settings>";
        using var reader = XmlReader.Create(new StringReader(doc));
        var s = SkylineTransitionSettings.Read(reader);

        Assert.Equal(0.0, s.ProductRes, 6);
        Assert.Equal(0.055, s.EffectiveProductTolerance, 6); // falls back to instrument mz_match_tolerance
    }
}
