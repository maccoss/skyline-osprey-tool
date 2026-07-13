using System.Globalization;

namespace OspreyTool.Core;

/// <summary>
/// Reads Skyline's chromatogram export (SkylineCmd/RunCommandSilent --chromatogram-file)
/// and groups it into <see cref="PrecursorChromatograms"/> per (FileName, precursor).
///
/// Verified format (docs/data-formats.md): tab-delimited, 10 columns, one row per
/// (transition, replicate); Times/Intensities are comma-separated numeric arrays inside a
/// single cell, equal length within a row, exported in the invariant locale. The RT grid is
/// per (precursor, replicate) - NOT global - so each row carries its own arrays.
/// </summary>
public static class ChromatogramTsvReader
{
    public const string ExpectedHeader =
        "FileName\tPeptideModifiedSequence\tPrecursorCharge\tProductMz\tFragmentIon\t" +
        "ProductCharge\tIsotopeLabelType\tTotalArea\tTimes\tIntensities";

    private const int ColFileName = 0;
    private const int ColPeptide = 1;
    private const int ColPrecursorCharge = 2;
    private const int ColProductMz = 3;
    private const int ColFragmentIon = 4;
    private const int ColProductCharge = 5;
    private const int ColTotalArea = 7;
    private const int ColTimes = 8;
    private const int ColIntensities = 9;
    private const int ColumnCount = 10;

    /// <summary>Reads and groups an export file into per-(file, precursor) chromatograms.</summary>
    public static IReadOnlyList<PrecursorChromatograms> ReadGroups(string path)
    {
        using var reader = new StreamReader(path);
        return ReadGroups(reader);
    }

    /// <summary>Reads and groups from an arbitrary reader (used by tests with in-memory data).</summary>
    public static IReadOnlyList<PrecursorChromatograms> ReadGroups(TextReader reader)
    {
        var order = new List<(string file, string pep, int charge)>();
        var groups = new Dictionary<(string, string, int), List<XicData>>();

        var header = reader.ReadLine();
        if (header is null)
        {
            return Array.Empty<PrecursorChromatograms>();
        }
        ValidateHeader(header);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length != ColumnCount)
            {
                throw new FormatException(
                    $"Expected {ColumnCount} tab-delimited columns, found {fields.Length}.");
            }

            var key = (fields[ColFileName], fields[ColPeptide], ParseInt(fields[ColPrecursorCharge]));
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<XicData>();
                groups[key] = list;
                order.Add(key);
            }

            list.Add(new XicData
            {
                FragmentIndex = list.Count,
                RetentionTimes = ParseArray(fields[ColTimes]),
                Intensities = ParseArray(fields[ColIntensities]),
                FragmentIon = fields[ColFragmentIon],
                ProductMz = ParseDouble(fields[ColProductMz]),
                ProductCharge = ParseInt(fields[ColProductCharge]),
                TotalArea = ParseDouble(fields[ColTotalArea]),
            });
        }

        var result = new List<PrecursorChromatograms>(order.Count);
        foreach (var key in order)
        {
            result.Add(new PrecursorChromatograms
            {
                FileName = key.file,
                PeptideModifiedSequence = key.pep,
                PrecursorCharge = key.charge,
                Xics = groups[key],
            });
        }
        return result;
    }

    private static void ValidateHeader(string header)
    {
        if (!header.StartsWith("FileName\t", StringComparison.Ordinal))
        {
            throw new FormatException(
                $"Unexpected chromatogram header. Expected:\n{ExpectedHeader}\nGot:\n{header}");
        }
    }

    private static double[] ParseArray(string cell)
    {
        if (cell.Length == 0)
        {
            return Array.Empty<double>();
        }
        var parts = cell.Split(',');
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            values[i] = ParseDouble(parts[i]);
        }
        return values;
    }

    private static double ParseDouble(string s) =>
        double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int ParseInt(string s) =>
        int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
