namespace OspreyTool.Core;

/// <summary>
/// One fragment's extracted ion chromatogram: parallel retention-time / intensity arrays.
/// Deliberately mirrors the shape of Osprey.Chromatography.XicData (int FragmentIndex +
/// double[] RetentionTimes + double[] Intensities) so it can be adapted at the Osprey.Api
/// boundary without a copy. The extra provenance fields come from the Skyline chromatogram
/// export and are ignored by Osprey CWT but useful for joins / the review UI.
/// </summary>
public sealed class XicData
{
    /// <summary>Order of this fragment within its precursor group (0 = first row seen).</summary>
    public required int FragmentIndex { get; init; }

    public required double[] RetentionTimes { get; init; }

    public required double[] Intensities { get; init; }

    /// <summary>Skyline FragmentIon label, e.g. "precursor", "y7", "b2".</summary>
    public string FragmentIon { get; init; } = "";

    public double ProductMz { get; init; }

    public int ProductCharge { get; init; }

    /// <summary>Skyline-reported integrated area for this transition in this replicate.</summary>
    public double TotalArea { get; init; }

    public bool IsPrecursor => FragmentIon.Equals("precursor", StringComparison.OrdinalIgnoreCase);

    public int PointCount => RetentionTimes.Length;
}
