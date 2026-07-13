using OspreyTool.Skyline;
using Xunit;

namespace OspreyTool.Tests;

public sealed class SkylineColorSchemeTests
{
    // Trimmed from what a live Skyline returned for GetSettingsListItem("Color Schemes", "Skyline classic").
    // Note Skyline's own attribute value is the misspelling "percursor".
    private const string ClassicXml = """
        <ColorScheme name="Skyline classic">
          <color type="percursor" red="255" green="0" blue="0" />
          <color type="percursor" red="0" green="0" blue="255" />
          <color type="transition" red="0" green="0" blue="255" />
          <color type="transition" red="138" green="43" blue="226" />
          <color type="transition" red="165" green="42" blue="42" />
        </ColorScheme>
        """;

    [Fact]
    public void Parses_precursor_and_transition_colors_including_Skylines_misspelling()
    {
        var (precursors, transitions) = SkylineColorScheme.Parse(ClassicXml);

        Assert.Equal(2, precursors.Count);
        Assert.Equal(new SchemeColor(255, 0, 0), precursors[0]);

        Assert.Equal(3, transitions.Count);
        Assert.Equal(new SchemeColor(0, 0, 255), transitions[0]);      // Blue
        Assert.Equal(new SchemeColor(138, 43, 226), transitions[1]);   // BlueViolet
        Assert.Equal(new SchemeColor(165, 42, 42), transitions[2]);    // Brown
    }

    [Fact]
    public void Falls_back_to_the_classic_palette_on_missing_or_broken_xml()
    {
        foreach (var xml in new[] { null, "", "not xml at all", "<ColorScheme name=\"empty\" />" })
        {
            var (_, transitions) = SkylineColorScheme.Parse(xml);
            Assert.Equal(SkylineColorScheme.ClassicTransitions, transitions);
        }
    }

    /// <summary>Skyline offsets product colours past the precursor colours so a products-only graph does not
    /// reuse them (GraphChromatogram.cs). Matching that offset is what makes our plot agree with Skyline's.</summary>
    [Fact]
    public void Product_colors_start_after_the_precursor_colors()
    {
        var palette = SkylineColorScheme.ClassicTransitions;

        // A document with one precursor trace: the first product takes the SECOND transition colour.
        Assert.Equal(palette[1], SkylineColorScheme.ColorFor(palette, productIndex: 0, precursorCount: 1));
        Assert.Equal(palette[2], SkylineColorScheme.ColorFor(palette, productIndex: 1, precursorCount: 1));

        // No precursor trace: products start at the first colour.
        Assert.Equal(palette[0], SkylineColorScheme.ColorFor(palette, productIndex: 0, precursorCount: 0));

        // And the palette wraps rather than running off the end.
        Assert.Equal(palette[0], SkylineColorScheme.ColorFor(palette, palette.Count, 0));
    }
}
