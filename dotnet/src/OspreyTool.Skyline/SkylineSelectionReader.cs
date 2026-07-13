#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace OspreyTool.Skyline;

/// <summary>
/// Reads what the user has selected in Skyline's Targets tree, so the tool's Candidate Peaks panel can
/// follow the document instead of making the user retype a peptide.
///
/// The JSON-RPC seam has no push notification for selection changes, so callers POLL <see cref="Read"/>
/// (cheap: two locator calls + the replicate name) and act only when the value changes.
///
/// ElementLocator syntax (Skyline): <c>Type:/group/molecule/precursor</c>, segments separated by '/', with
/// a segment quoted ("...", inner quotes doubled) when it contains a separator. Peptides are named by their
/// modified sequence, and a precursor by label type + one sign per charge, e.g.
/// <c>Precursor:/sp|P36222|CH3L1_HUMAN/ILGQQVPYATK/light++</c> -> ILGQQVPYATK, charge 2.
/// </summary>
public static class SkylineSelectionReader
{
    /// <summary>Current selection. Skyline walks UP the tree, so selecting a transition still reports its
    /// Molecule; charge is null when the selection is above the precursor level.</summary>
    public static SkylineSelection Read(ISkylineClient client)
    {
        var peptide = LastSegment(client.GetSelectedElementLocator("Molecule"));
        var charge = ChargeFromPrecursorLocator(client.GetSelectedElementLocator("Precursor"));
        var replicate = client.GetReplicateName();
        return new SkylineSelection(peptide, charge, string.IsNullOrEmpty(replicate) ? null : replicate);
    }

    /// <summary>The final path segment of an ElementLocator (for a Molecule: the modified sequence).</summary>
    public static string? LastSegment(string? locator)
    {
        var segments = Segments(locator);
        return segments.Count > 0 ? segments[^1] : null;
    }

    /// <summary>Charge from a Precursor locator's trailing sign run ("light++" -> 2, "heavy---" -> -3).</summary>
    public static int? ChargeFromPrecursorLocator(string? locator)
    {
        var last = LastSegment(locator);
        if (string.IsNullOrEmpty(last))
        {
            return null;
        }
        var sign = last[^1];
        if (sign != '+' && sign != '-')
        {
            return null;
        }
        var count = 0;
        for (var i = last.Length - 1; i >= 0 && last[i] == sign; i--)
        {
            count++;
        }
        return sign == '+' ? count : -count;
    }

    /// <summary>Splits the locator's path on '/', honoring quoted segments and dropping the "Type:" prefix.</summary>
    private static List<string> Segments(string? locator)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(locator))
        {
            return result;
        }

        // Strip the element-type prefix ("Molecule:/..."); a bare "/..." path is accepted too.
        var start = 0;
        var colon = locator.IndexOf(':');
        var slash = locator.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash))
        {
            start = colon + 1;
        }

        var sb = new StringBuilder();
        var inQuotes = false;
        var any = false;
        for (var i = start; i < locator.Length; i++)
        {
            var ch = locator[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < locator.Length && locator[i + 1] == '"')
                    {
                        sb.Append('"'); // doubled quote = a literal one
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
                continue;
            }
            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    any = true;
                    break;
                case '/':
                    if (any)
                    {
                        result.Add(sb.ToString());
                    }
                    sb.Clear();
                    any = false;
                    break;
                default:
                    sb.Append(ch);
                    any = true;
                    break;
            }
        }
        if (any)
        {
            result.Add(sb.ToString());
        }
        return result;
    }
}
