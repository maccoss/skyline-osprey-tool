#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace OspreyTool.Skyline;

/// <summary>An RGB colour from a Skyline colour scheme (no WPF/WinForms dependency in this project).</summary>
public readonly record struct SchemeColor(byte R, byte G, byte B);

/// <summary>
/// The chromatogram colours Skyline draws with, so the tool's XIC plot matches the Targets/chromatogram
/// graphs the analyst is looking at next to it.
///
/// Skyline stores these in the "Color Schemes" settings list (Tools &gt; Options &gt; Display), which the RPC
/// seam can read as XML - so we read the user's ACTUAL scheme (including any custom one they made) rather
/// than hardcoding a palette. Which scheme is *active* is an application setting and is NOT exposed over the
/// seam, so the tool lets the user pick the scheme and defaults to "Skyline classic".
/// </summary>
public static class SkylineColorScheme
{
    public const string DefaultName = "Skyline classic";
    public const string ListType = "Color Schemes";

    /// <summary>"Skyline classic" transition colours, for standalone mode / when the read fails.</summary>
    public static readonly IReadOnlyList<SchemeColor> ClassicTransitions = new[]
    {
        new SchemeColor(0, 0, 255),      // Blue
        new SchemeColor(138, 43, 226),   // BlueViolet
        new SchemeColor(165, 42, 42),    // Brown
        new SchemeColor(210, 105, 30),   // Chocolate
        new SchemeColor(0, 139, 139),    // DarkCyan
        new SchemeColor(0, 128, 0),      // Green
        new SchemeColor(255, 165, 0),    // Orange
        new SchemeColor(117, 112, 179),
        new SchemeColor(128, 0, 128),    // Purple
        new SchemeColor(50, 205, 50),    // LimeGreen
        new SchemeColor(255, 215, 0),    // Gold
        new SchemeColor(255, 0, 255),    // Magenta
        new SchemeColor(128, 0, 0),      // Maroon
        new SchemeColor(107, 142, 35),   // OliveDrab
        new SchemeColor(65, 105, 225),   // RoyalBlue
    };

    /// <summary>Skyline's classic precursor colours - only their COUNT matters to us (see <see cref="ColorFor"/>),
    /// but a scheme carries both lists and we parse both.</summary>
    public static readonly IReadOnlyList<SchemeColor> ClassicPrecursors = new[]
    {
        new SchemeColor(255, 0, 0), new SchemeColor(0, 0, 255), new SchemeColor(128, 0, 0),
        new SchemeColor(128, 0, 128), new SchemeColor(255, 165, 0), new SchemeColor(0, 128, 0),
        new SchemeColor(255, 255, 0), new SchemeColor(173, 216, 230),
    };

    /// <summary>
    /// Parses a Skyline <c>&lt;ColorScheme&gt;</c> XML document (as returned by GetSettingsListItem) into its
    /// precursor and transition colour lists. Returns the classic palette if the XML is unusable.
    /// </summary>
    public static (IReadOnlyList<SchemeColor> Precursors, IReadOnlyList<SchemeColor> Transitions) Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return (ClassicPrecursors, ClassicTransitions);
        }
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null)
            {
                return (ClassicPrecursors, ClassicTransitions);
            }
            var precursors = new List<SchemeColor>();
            var transitions = new List<SchemeColor>();
            foreach (var el in root.Elements("color"))
            {
                if (!TryColor(el, out var c))
                {
                    continue;
                }
                // Skyline writes the misspelling "percursor" (ColorScheme.cs GROUP_NAME_PRECURSOR; reported
                // as ProteoWizard/pwiz#4415). Match it exactly, and accept the correct spelling too in case
                // it is fixed upstream - but match nothing else, so an unknown or future colour type is
                // ignored rather than silently treated as a precursor colour.
                var type = (string?)el.Attribute("type") ?? "";
                if (type.Equals("transition", StringComparison.OrdinalIgnoreCase))
                {
                    transitions.Add(c);
                }
                else if (type.Equals("percursor", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("precursor", StringComparison.OrdinalIgnoreCase))
                {
                    precursors.Add(c);
                }
            }
            return (
                precursors.Count > 0 ? precursors : ClassicPrecursors,
                transitions.Count > 0 ? transitions : ClassicTransitions);
        }
        catch (Exception)
        {
            return (ClassicPrecursors, ClassicTransitions);
        }
    }

    /// <summary>
    /// The colour Skyline gives the <paramref name="productIndex"/>'th product ion of a precursor.
    /// Skyline offsets product colours past the precursor colours so a products-only (or split) graph does not
    /// reuse them - the offset is the number of PRECURSOR transitions in the group (GraphChromatogram.cs), which
    /// for a Skyline chromatogram export is simply the number of "precursor" rows.
    /// </summary>
    public static SchemeColor ColorFor(IReadOnlyList<SchemeColor> transitions, int productIndex, int precursorCount)
    {
        if (transitions.Count == 0)
        {
            transitions = ClassicTransitions;
        }
        var i = (productIndex + precursorCount) % transitions.Count;
        return transitions[i < 0 ? i + transitions.Count : i];
    }

    private static bool TryColor(XElement el, out SchemeColor color)
    {
        color = default;
        if (!TryByte(el, "red", out var r) || !TryByte(el, "green", out var g) || !TryByte(el, "blue", out var b))
        {
            return false;
        }
        color = new SchemeColor(r, g, b);
        return true;
    }

    private static bool TryByte(XElement el, string name, out byte value)
    {
        value = 0;
        var a = (string?)el.Attribute(name);
        if (a is null || !int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }
        value = (byte)Math.Clamp(v, 0, 255);
        return true;
    }
}
