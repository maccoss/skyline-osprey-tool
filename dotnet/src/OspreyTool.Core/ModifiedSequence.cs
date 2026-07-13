using System.Globalization;
using System.Text.RegularExpressions;

namespace OspreyTool.Core;

/// <summary>
/// Normalizes modified-sequence strings so the same peptide matches across sources that write
/// modification masses differently. Skyline exports <c>C[+57]</c>; a Carafe/Bibliospec .blib writes
/// <c>C[57.02146372057]</c> - both normalize to <c>C[+57]</c> by rounding each bracketed mass to a
/// signed integer. Rounding keeps distinct variable mods distinct (e.g. <c>M[+16]</c>), and leaves
/// non-numeric bracket contents (named mods) untouched.
/// </summary>
public static class ModifiedSequence
{
    private static readonly Regex BracketMass = new(@"\[([^\]]+)\]", RegexOptions.Compiled);

    public static string Normalize(string modifiedSequence) =>
        BracketMass.Replace(modifiedSequence, match =>
        {
            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mass))
            {
                var rounded = (int)Math.Round(mass, MidpointRounding.AwayFromZero);
                return rounded >= 0 ? $"[+{rounded}]" : $"[{rounded}]";
            }
            return match.Value;
        });

    /// <summary>Strips every bracketed modification, leaving the bare amino-acid sequence
    /// (so <c>AGAQYVALC[+57]R</c> and <c>AGAQYVALCR</c> compare equal - used to match a decoy
    /// written plain in a transition list against Skyline's fixed-mod-applied export).</summary>
    public static string StripMods(string modifiedSequence) =>
        BracketMass.Replace(modifiedSequence, string.Empty);
}
