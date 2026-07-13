namespace OspreyTool.Core;

/// <summary>
/// Decoy-XIC generator by independent per-fragment circular time-shift. Each target fragment
/// trace is rotated along its own (shared) RT grid by a different, deterministic offset, so
/// the fragments no longer co-elute - the defining property of a false detection - while each
/// trace keeps its own intensity values (peak shape and area are preserved per fragment). The
/// co-elution / median-polish scores should therefore rank these decoys well below real
/// targets, which is what M0 tests.
///
/// Offsets are derived from a stable FNV-1a hash of (peptide, charge, fragment index, seed) -
/// NOT String.GetHashCode, which is randomized per process - so a given seed reproduces the
/// exact same decoys every run (required for stable FDR).
/// </summary>
public sealed class XicShuffleDecoyGenerator : IDecoyChromatogramGenerator
{
    public const string DecoyPrefix = "DECOY_";

    private readonly int _seed;

    public XicShuffleDecoyGenerator(int seed = 1337) => _seed = seed;

    public string MethodName => "xic-shuffle";

    /// <summary>True if a sequence was produced by this generator (carries the decoy prefix).</summary>
    public static bool IsDecoyLabel(string peptideModifiedSequence) =>
        peptideModifiedSequence.StartsWith(DecoyPrefix, StringComparison.Ordinal);

    public PrecursorChromatograms Generate(PrecursorChromatograms target)
    {
        var decoyXics = new List<XicData>(target.Xics.Count);
        foreach (var xic in target.Xics)
        {
            decoyXics.Add(new XicData
            {
                FragmentIndex = xic.FragmentIndex,
                RetentionTimes = xic.RetentionTimes,
                Intensities = ShiftedIntensities(xic, target.Key),
                FragmentIon = xic.FragmentIon,
                ProductMz = xic.ProductMz,
                ProductCharge = xic.ProductCharge,
                TotalArea = xic.TotalArea,
            });
        }

        return new PrecursorChromatograms
        {
            FileName = target.FileName,
            PeptideModifiedSequence = DecoyPrefix + target.PeptideModifiedSequence,
            PrecursorCharge = target.PrecursorCharge,
            Xics = decoyXics,
        };
    }

    private double[] ShiftedIntensities(XicData xic, PrecursorKey key)
    {
        var source = xic.Intensities;
        var n = source.Length;
        if (n < 2)
        {
            return (double[])source.Clone();
        }

        var offset = OffsetFor(key, xic.FragmentIndex, n);
        var shifted = new double[n];
        for (var j = 0; j < n; j++)
        {
            shifted[j] = source[((j - offset) % n + n) % n];
        }
        return shifted;
    }

    /// <summary>
    /// A per-fragment shift in grid points, kept away from 0 and n so the peak actually moves
    /// far enough to decorrelate from the other fragments.
    /// </summary>
    private int OffsetFor(PrecursorKey key, int fragmentIndex, int n)
    {
        var minShift = Math.Max(1, n / 8);
        var range = n - 2 * minShift;
        if (range <= 0)
        {
            return n / 2;
        }

        var hash = Fnv1a($"{key.PeptideModifiedSequence}|{key.PrecursorCharge}|{fragmentIndex}|{_seed}");
        return minShift + (int)(hash % (uint)range);
    }

    private static uint Fnv1a(string s)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var c in s)
        {
            hash ^= (byte)c;
            hash *= prime;
            hash ^= (byte)(c >> 8);
            hash *= prime;
        }
        return hash;
    }
}
