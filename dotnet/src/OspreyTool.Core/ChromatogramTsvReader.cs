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

    /// <summary>The columns this reader consumes, located BY NAME in the header (IsotopeLabelType is
    /// exported but unused). Never by fixed position: were Skyline to reorder or insert a column, a
    /// positional parser would silently read the wrong field - not hypothetical, that is exactly how an
    /// m/z column once got read as a retention time in this project.</summary>
    private static readonly string[] RequiredColumns =
    {
        "FileName", "PeptideModifiedSequence", "PrecursorCharge", "ProductMz", "FragmentIon",
        "ProductCharge", "TotalArea", "Times", "Intensities",
    };

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
        var col = MapColumns(header);
        var minFields = col.Values.Max() + 1;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < minFields)
            {
                throw new FormatException(
                    $"Expected at least {minFields} tab-delimited columns, found {fields.Length}.");
            }

            var key = (fields[col["FileName"]], fields[col["PeptideModifiedSequence"]],
                ParseInt(fields[col["PrecursorCharge"]]));
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<XicData>();
                groups[key] = list;
                order.Add(key);
            }

            list.Add(new XicData
            {
                FragmentIndex = list.Count,
                RetentionTimes = ParseArray(fields[col["Times"]]),
                Intensities = ParseArray(fields[col["Intensities"]]),
                FragmentIon = fields[col["FragmentIon"]],
                ProductMz = ParseDouble(fields[col["ProductMz"]]),
                ProductCharge = ParseInt(fields[col["ProductCharge"]]),
                TotalArea = ParseDouble(fields[col["TotalArea"]]),
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

    /// <summary>Locates every required column in the header by name, so a reordered or extended export is
    /// either read correctly or rejected outright - never silently mis-parsed.</summary>
    private static Dictionary<string, int> MapColumns(string header)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = header.Split('\t');
        for (var i = 0; i < names.Length; i++)
        {
            found[names[i].Trim()] = i;
        }

        var missing = RequiredColumns.Where(c => !found.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new FormatException(
                $"Chromatogram export is missing column(s): {string.Join(", ", missing)}.\n" +
                $"Expected (order does not matter):\n{ExpectedHeader}\nGot:\n{header}");
        }
        return RequiredColumns.ToDictionary(c => c, c => found[c], StringComparer.Ordinal);
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
