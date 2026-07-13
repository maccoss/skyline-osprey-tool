using System.Globalization;
using System.Xml;

namespace OspreyTool.Core;

/// <summary>
/// The transition settings that governed Skyline's chromatogram extraction, read from a `.sky`
/// document (or a settings-export XML). Osprey's fragment matching should use the SAME tolerances
/// Skyline extracted with, so these values drive the Osprey config rather than a hardcoded guess.
///
/// For the Stellar PRM test document: acquisition PRM, product/precursor analyzer `qit`
/// (unit-resolution ion trap), product_res 0.5, precursor_res 0.7.
/// </summary>
public sealed class SkylineTransitionSettings
{
    public string AcquisitionMethod { get; init; } = "";

    public string ProductMassAnalyzer { get; init; } = "";

    /// <summary>`transition_full_scan/@product_res` - m/z extraction width for product ions (Th on an ion trap).</summary>
    public double ProductRes { get; init; }

    public string PrecursorMassAnalyzer { get; init; } = "";

    /// <summary>`transition_full_scan/@precursor_res`.</summary>
    public double PrecursorRes { get; init; }

    /// <summary>`transition_instrument/@mz_match_tolerance` - fallback when full-scan res is absent.</summary>
    public double MzMatchTolerance { get; init; }

    /// <summary>True for ion-trap / unit-resolution product analyzers (fixed m/z window, not ppm).</summary>
    public bool IsUnitResolution => IsUnitResolutionAnalyzer(ProductMassAnalyzer);

    public static bool IsUnitResolutionAnalyzer(string analyzer) =>
        analyzer.Equals("qit", StringComparison.OrdinalIgnoreCase) ||
        analyzer.Equals("ion_trap", StringComparison.OrdinalIgnoreCase);

    public static SkylineTransitionSettings FromDocument(string skylineDocumentPath)
    {
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(skylineDocumentPath, settings);
        return Read(reader);
    }

    /// <summary>Scans the (early) transition_settings block and stops at its end - never reads the peptide tree.</summary>
    public static SkylineTransitionSettings Read(XmlReader reader)
    {
        var acquisition = "";
        var productAnalyzer = "";
        var precursorAnalyzer = "";
        double productRes = 0, precursorRes = 0, mzMatchTolerance = 0;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "transition_settings")
            {
                break;
            }
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.Name)
            {
                case "transition_full_scan":
                    acquisition = reader.GetAttribute("acquisition_method") ?? "";
                    productAnalyzer = reader.GetAttribute("product_mass_analyzer") ?? "";
                    precursorAnalyzer = reader.GetAttribute("precursor_mass_analyzer") ?? "";
                    productRes = ParseDouble(reader.GetAttribute("product_res"));
                    precursorRes = ParseDouble(reader.GetAttribute("precursor_res"));
                    break;
                case "transition_instrument":
                    mzMatchTolerance = ParseDouble(reader.GetAttribute("mz_match_tolerance"));
                    break;
            }
        }

        return new SkylineTransitionSettings
        {
            AcquisitionMethod = acquisition,
            ProductMassAnalyzer = productAnalyzer,
            ProductRes = productRes,
            PrecursorMassAnalyzer = precursorAnalyzer,
            PrecursorRes = precursorRes,
            MzMatchTolerance = mzMatchTolerance,
        };
    }

    /// <summary>The effective product m/z tolerance to hand Osprey (full-scan res, else instrument match tolerance).</summary>
    public double EffectiveProductTolerance => ProductRes > 0 ? ProductRes : MzMatchTolerance;

    /// <summary>The effective precursor m/z tolerance (precursor res, else the product tolerance).</summary>
    public double EffectivePrecursorTolerance => PrecursorRes > 0 ? PrecursorRes : EffectiveProductTolerance;

    private static double ParseDouble(string? s) =>
        string.IsNullOrEmpty(s) ? 0 : double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
