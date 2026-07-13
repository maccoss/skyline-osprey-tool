using OspreyTool.Core.Detection;
using OspreyTool.Scoring.Detection;
using pwiz.Osprey.Chromatography;
using pwiz.Osprey.Core;
using pwiz.Osprey.Scoring;
using CoreXic = OspreyTool.Core.XicData;
using CoreLibFragment = OspreyTool.Core.LibraryFragment;

namespace OspreyTool.Scoring;

/// <summary>
/// Runs Osprey's CWT peak detection and the 12-feature XIC-only subset over Skyline-extracted
/// chromatograms for one candidate. This is the heart of the (prototype) Osprey.Api facade: it
/// wraps our XICs in an <see cref="XicPeakData"/> adapter, publishes the median-polish
/// byproduct the way Osprey's own harness does, and invokes the public calculator registry.
///
/// Reusable across candidates (target and decoy) - the scoring context is reset per call.
/// </summary>
public sealed class OspreyFeatureScorer
{
    /// <summary>
    /// The XIC-only + RT + median-polish subset (docs/osprey-api.md): coelution sum/max/count,
    /// peak apex/area/sharpness, rt/abs-rt deviation, and the four median-polish scores.
    /// Excludes MS1 (13,14), apex-spectrum (7-10), and xcorr (6,17,18) families.
    /// </summary>
    public static readonly int[] SubsetFeatureIndices = { 0, 1, 2, 3, 4, 5, 11, 12, 15, 16, 19, 20 };

    private readonly OspreyScoringContext _context;
    private readonly IPeakDetector _detector;

    /// <summary>Candidate peaks come from <paramref name="detector"/> (default: Osprey's CWT). Scoring,
    /// ranking, FDR and reconciliation are unchanged by the choice - see <see cref="IPeakDetector"/>.</summary>
    public OspreyFeatureScorer(OspreyConfig config, IPeakDetector? detector = null)
    {
        _context = new OspreyScoringContext(config);
        _detector = detector ?? PeakDetectors.Default;
    }

    /// <summary>The detector supplying candidate peaks.</summary>
    public IPeakDetector Detector => _detector;

    /// <summary>A scorer with a default Osprey config - lets callers that don't reference the Osprey
    /// assembly (e.g. the CLI) create one without naming <see cref="OspreyConfig"/>.</summary>
    public static OspreyFeatureScorer CreateDefault(IPeakDetector? detector = null) =>
        new(new OspreyConfig(), detector);

    /// <summary>Config for unit-resolution PRM (Stellar): unit-resolution matching + a Da tolerance.</summary>
    public static OspreyConfig UnitResolutionConfig(double fragmentToleranceDa = 0.5) => new()
    {
        ResolutionMode = ResolutionMode.UnitResolution,
        FragmentTolerance = FragmentToleranceConfig.UnitResolution(fragmentToleranceDa),
    };

    /// <summary>
    /// Detects the best consensus peak and computes the 12-feature subset for one candidate.
    /// <paramref name="xicSet"/> are the fragment XICs on a shared RT grid; <paramref name="expectedRt"/>
    /// is the library's predicted RT (drives rt-deviation). Returns <see cref="ScoredCandidate.NoPeak"/>
    /// when CWT finds no peak (needs >=2 XICs, >=5 equal-length scans).
    /// </summary>
    public ScoredCandidate Score(
        LibraryEntry candidate,
        IReadOnlyList<CoreXic> xicSet,
        double expectedRt,
        double minConsensusHeight = 0.0)
    {
        // Co-elution is fragment-to-fragment; exclude the precursor isotope trace. Need >=2 fragments.
        var xics = new List<XicData>(xicSet.Count);
        foreach (var x in xicSet)
        {
            if (x.IsPrecursor)
            {
                continue;
            }
            xics.Add(new XicData(x.FragmentIndex, x.RetentionTimes, x.Intensities));
        }
        if (xics.Count < 2)
        {
            return ScoredCandidate.NoPeak(candidate);
        }

        var peaks = DetectPeaks(xics, minConsensusHeight, expectedRt);
        if (peaks.Count == 0)
        {
            return ScoredCandidate.NoPeak(candidate);
        }

        var bounds = peaks[0];
        var adapter = new XicPeakData(candidate, bounds, ApexRt(xics, bounds), expectedRt, xics);

        _context.ClearByproducts();
        PublishMedianPolish(bounds, xics);

        var features = new double[OspreyFeatureCalculators.FeatureCount];
        foreach (var i in SubsetFeatureIndices)
        {
            features[i] = OspreyFeatureCalculators.Get(i).Calculate(_context, adapter);
        }

        return new ScoredCandidate(candidate, bounds, features, hasPeak: true);
    }

    private static double ApexRt(List<XicData> xics, XICPeakBounds bounds)
    {
        if (xics.Count == 0)
        {
            return bounds.ApexRt;
        }
        var rts = xics[0].RetentionTimes;
        return bounds.ApexIndex >= 0 && bounds.ApexIndex < rts.Length ? rts[bounds.ApexIndex] : bounds.ApexRt;
    }

    /// <summary>
    /// Re-picks the peak for one precursor with Osprey's full bestPeak rank score (mirrors
    /// PeakDataExtractor.TryExtract):
    ///   rank = coelution * exp(-dt^2 / 2*sigma^2) * ln(1 + apex_intensity)
    /// - coelution = mean pairwise Pearson correlation of the fragment XICs over the peak window
    ///   (Osprey's public <c>ScoringMath.PearsonCorrelationInRange</c>, so byte-identical);
    /// - dt = |chosen apex RT - <paramref name="expectedRt"/>| (the library predicted RT);
    /// - apex_intensity = the reference (highest-total-intensity) fragment's value at the apex.
    /// Candidates whose apex is beyond <paramref name="rtTolerance"/> of the expected RT are rejected.
    /// With no <paramref name="expectedRt"/> the RT penalty is 1 and the gate is skipped (co-elution +
    /// intensity only). Runs on target chromatograms alone - no library entry needed.
    /// </summary>
    /// <summary>
    /// The features used for the second-best-peak (mProphet-style) FDR - the XIC/RT/median-polish subset
    /// that needs no library entry (excludes median_polish_cosine (15), which reads the theoretical
    /// fragments, and the MS1/apex/xcorr families).
    /// </summary>
    public static readonly int[] FdrFeatureIndices = { 0, 1, 2, 3, 4, 5, 11, 12, 16, 19, 20 };

    /// <summary>
    /// Scores an ARBITRARY RT window with exactly the terms <see cref="Repick"/> ranks CWT candidates by.
    /// Reconciliation can force a boundary that is not a CWT candidate at all (MBR force-integration at the
    /// cross-run consensus RT); this lets the tool show that window side by side with the peak the signal
    /// processing chose, on the same scale. Returns null if there are &lt;2 fragments or the window is empty.
    /// </summary>
    public CandidatePeak? ScoreWindow(
        IReadOnlyList<CoreXic> xicSet,
        double startRt,
        double endRt,
        double? expectedRt,
        double rtSigma,
        IReadOnlyList<CoreLibFragment>? libraryFragments = null,
        double intensityExponent = 1.0)
    {
        var xics = new List<XicData>(xicSet.Count);
        var xicProductMz = new List<double>(xicSet.Count);
        foreach (var x in xicSet)
        {
            if (x.IsPrecursor)
            {
                continue;
            }
            xics.Add(new XicData(xics.Count, x.RetentionTimes, x.Intensities));
            xicProductMz.Add(x.ProductMz);
        }
        if (xics.Count < 2)
        {
            return null;
        }

        var rts = xics[0].RetentionTimes;
        var startIndex = NearestIndex(rts, startRt);
        var endIndex = NearestIndex(rts, endRt);
        if (endIndex < startIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
        }

        var refIntensities = xics[ReferenceIndex(xics)].Intensities;
        var apexIndex = startIndex;
        for (var i = startIndex; i <= endIndex && i < refIntensities.Length; i++)
        {
            if (refIntensities[i] > refIntensities[apexIndex])
            {
                apexIndex = i;
            }
        }

        var libFrags = BuildAlignedLibFragments(xicProductMz, libraryFragments);
        var (coelution, libCosine, rtPenalty, intensityWeight, dt) = WindowTerms(
            xics, libFrags, refIntensities, startIndex, endIndex, apexIndex, rts,
            expectedRt, 2.0 * rtSigma * rtSigma, intensityExponent);

        return new CandidatePeak
        {
            StartRt = RtAt(rts, startIndex),
            ApexRt = RtAt(rts, apexIndex),
            EndRt = RtAt(rts, endIndex),
            Coelution = coelution,
            LibCosine = libCosine,
            RtResidual = dt,
            RtPenalty = rtPenalty,
            IntensityWeight = intensityWeight,
            Rank = coelution * libCosine * rtPenalty * intensityWeight,
        };
    }

    public RepickResult Repick(
        IReadOnlyList<CoreXic> xicSet,
        double? expectedRt,
        double rtTolerance,
        double rtSigma,
        double minConsensusHeight = 0.0,
        bool computeFdrFeatures = false,
        bool retainCandidates = false,
        IReadOnlyList<CoreLibFragment>? libraryFragments = null,
        bool traceCandidates = false,
        double intensityExponent = 1.0)
    {
        // Co-elution is fragment-to-fragment; exclude the precursor isotope trace. Need >=2 fragments.
        // Re-index the fragments 0..n-1 (not the export order) so the median-polish row keys line up with
        // the by-m/z-aligned library-intensity vector used for the library cosine.
        var xics = new List<XicData>(xicSet.Count);
        var xicProductMz = new List<double>(xicSet.Count);
        foreach (var x in xicSet)
        {
            if (x.IsPrecursor)
            {
                continue;
            }
            xics.Add(new XicData(xics.Count, x.RetentionTimes, x.Intensities));
            xicProductMz.Add(x.ProductMz);
        }
        if (xics.Count < 2)
        {
            return RepickResult.TooFewFragments();
        }

        // Library-intensity vector aligned to the fragment XICs by product m/z, for median_polish_cosine.
        var libFrags = BuildAlignedLibFragments(xicProductMz, libraryFragments);

        var peaks = DetectPeaks(xics, minConsensusHeight, expectedRt);
        if (peaks.Count == 0)
        {
            return RepickResult.NoCandidates();
        }

        var rts = xics[0].RetentionTimes;

        // Retain every CWT peak (pre-gate) as a reconciliation candidate. Only apex/start/end RT are read
        // by ReconciliationPlanner; area/SNR/coelution are left at 0.
        List<pwiz.Osprey.Core.CwtCandidate>? allCandidates = null;
        if (retainCandidates)
        {
            allCandidates = new List<pwiz.Osprey.Core.CwtCandidate>(peaks.Count);
            foreach (var p in peaks)
            {
                allCandidates.Add(new pwiz.Osprey.Core.CwtCandidate
                {
                    ApexRt = RtAt(rts, p.ApexIndex),
                    StartRt = RtAt(rts, p.StartIndex),
                    EndRt = RtAt(rts, p.EndIndex),
                });
            }
        }

        var refIntensities = xics[ReferenceIndex(xics)].Intensities;

        var twoSigmaSq = 2.0 * rtSigma * rtSigma;
        var cands = new List<(XICPeakBounds peak, int cwtRank, double rank, double coelution, double residual,
            double libCosine, double rtPenalty, double intensityWeight)>(peaks.Count);
        for (var i = 0; i < peaks.Count; i++)
        {
            var p = peaks[i];
            if (p.EndIndex - p.StartIndex + 1 < 3)
            {
                continue;
            }

            var apexRt = RtAt(rts, p.ApexIndex);
            var dt = expectedRt.HasValue ? Math.Abs(apexRt - expectedRt.Value) : 0.0;
            // For scheduled PRM the extracted XIC IS the instrument scheduling window - there is no data
            // outside it - so the window itself is the hard RT limit; every candidate here is already in it.
            // RT only enters as the soft exp(-dt^2/2sigma^2) penalty below. A positive rtTolerance can
            // still re-impose a hard apex-acceptance gate (non-PRM / diagnostics); <=0 disables it.
            if (expectedRt.HasValue && rtTolerance > 0.0 && dt > rtTolerance)
            {
                continue;
            }

            var (coelution, libCosine, rtPenalty, intensityWeight, _) = WindowTerms(
                xics, libFrags, refIntensities, p.StartIndex, p.EndIndex, p.ApexIndex, rts,
                expectedRt, twoSigmaSq, intensityExponent);

            cands.Add((p, i, coelution * libCosine * rtPenalty * intensityWeight, coelution, dt,
                libCosine, rtPenalty, intensityWeight));
        }

        if (cands.Count == 0)
        {
            // CWT peaks found, none in RT tolerance. Keep the candidates so reconciliation can rescue it.
            return new RepickResult { HasPeak = false, CandidateCount = peaks.Count, Candidates = allCandidates };
        }

        // Best peak = highest rank score.
        var bestI = 0;
        for (var k = 1; k < cands.Count; k++)
        {
            if (cands[k].rank > cands[bestI].rank)
            {
                bestI = k;
            }
        }

        // Second-best null = highest-rank candidate whose RT range does NOT overlap the best peak.
        // Excluding overlapping candidates drops shoulders / split-peaks of the same elution (which
        // co-elute just as well) so the null is a genuinely different peak.
        var secondI = -1;
        for (var k = 0; k < cands.Count; k++)
        {
            if (k == bestI || Overlaps(cands[k].peak, cands[bestI].peak))
            {
                continue;
            }
            if (secondI < 0 || cands[k].rank > cands[secondI].rank)
            {
                secondI = k;
            }
        }

        // For the multi-feature FDR (A): score the full XIC/RT/median-polish feature vector for BOTH the
        // best peak (the target example) and the non-overlapping second-best peak (the null example), so
        // Percolator can learn a discriminant richer than co-elution alone. Uses a stub library entry
        // because FdrFeatureIndices deliberately excludes every feature that reads theoretical fragments.
        double[]? bestFeatures = null;
        double[]? secondFeatures = null;
        if (computeFdrFeatures)
        {
            var stub = new LibraryEntry(1u, "x", "x", 1, 0.0, 0.0);
            var exp = expectedRt ?? RtAt(rts, cands[bestI].peak.ApexIndex);
            bestFeatures = ComputeSubsetFeatures(xics, cands[bestI].peak, exp, stub, FdrFeatureIndices);
            if (secondI >= 0)
            {
                secondFeatures = ComputeSubsetFeatures(xics, cands[secondI].peak, exp, stub, FdrFeatureIndices);
            }
        }

        List<CandidatePeak>? trace = null;
        if (traceCandidates)
        {
            trace = new List<CandidatePeak>(cands.Count);
            // CWT reports the same window more than once (a peak surviving at several wavelet scales). That is
            // harmless for ranking, but it clutters the trace, so collapse identical windows for DISPLAY only -
            // the candidate list handed to reconciliation is left exactly as Osprey produced it.
            var seenWindows = new HashSet<(int, int)>();
            for (var k = 0; k < cands.Count; k++)
            {
                var c = cands[k];
                var isPick = k == bestI || k == secondI;
                if (!seenWindows.Add((c.peak.StartIndex, c.peak.EndIndex)) && !isPick)
                {
                    continue;
                }
                trace.Add(new CandidatePeak
                {
                    StartRt = RtAt(rts, c.peak.StartIndex),
                    ApexRt = RtAt(rts, c.peak.ApexIndex),
                    EndRt = RtAt(rts, c.peak.EndIndex),
                    Coelution = c.coelution,
                    LibCosine = c.libCosine,
                    RtResidual = c.residual,
                    RtPenalty = c.rtPenalty,
                    IntensityWeight = c.intensityWeight,
                    Rank = c.rank,
                    Chosen = k == bestI,
                    SecondBest = k == secondI,
                });
            }
        }

        var chosen = cands[bestI].peak;
        return new RepickResult
        {
            HasPeak = true,
            StartRt = RtAt(rts, chosen.StartIndex),
            EndRt = RtAt(rts, chosen.EndIndex),
            ApexRt = RtAt(rts, chosen.ApexIndex),
            Coelution = cands[bestI].coelution,
            RankScore = cands[bestI].rank,
            CandidatePeaks = trace,
            HasSecondBest = secondI >= 0,
            SecondBestCoelution = secondI >= 0 ? cands[secondI].coelution : double.NaN,
            ExpectedRt = expectedRt ?? double.NaN,
            RtResidual = cands[bestI].residual,
            CandidateCount = peaks.Count,
            ScoredCount = cands.Count,
            ChosenCwtRank = cands[bestI].cwtRank,
            BestFeatures = bestFeatures,
            SecondBestFeatures = secondFeatures,
            Candidates = allCandidates,
        };
    }

    private static bool Overlaps(XICPeakBounds a, XICPeakBounds b) =>
        a.StartIndex <= b.EndIndex && b.StartIndex <= a.EndIndex;

    /// <summary>
    /// Computes the requested feature subset for a specific peak (the given <paramref name="bounds"/>),
    /// publishing the median-polish byproduct at that window first - the same machinery as <see cref="Score"/>
    /// but for an arbitrary peak rather than only CWT's top pick. Returns a full 21-length vector with only
    /// <paramref name="indices"/> populated (the rest 0, which the Percolator standardizer treats as zero-variance).
    /// </summary>
    private double[] ComputeSubsetFeatures(
        List<XicData> xics, XICPeakBounds bounds, double expectedRt, LibraryEntry candidate, int[] indices)
    {
        var adapter = new XicPeakData(candidate, bounds, ApexRt(xics, bounds), expectedRt, xics);
        _context.ClearByproducts();
        PublishMedianPolish(bounds, xics);
        var features = new double[OspreyFeatureCalculators.FeatureCount];
        foreach (var i in indices)
        {
            features[i] = OspreyFeatureCalculators.Get(i).Calculate(_context, adapter);
        }
        return features;
    }

    private static double RtAt(double[] rts, int index) =>
        index >= 0 && index < rts.Length ? rts[index] : double.NaN;

    /// <summary>
    /// Publishes the Tukey median-polish byproduct that features 15/16/19/20 consume, exactly
    /// as Osprey's CoelutionScorer.ScoreCandidate does: crop each XIC to the peak window
    /// [StartIndex..EndIndex], fit with maxIter=10 / tol=0.01, and publish only if it converged.
    /// Without this the four median-polish features silently return defaults (residual_ratio 1.0).
    /// </summary>
    private void PublishMedianPolish(XICPeakBounds bounds, List<XicData> xics)
    {
        var slices = BuildPeakSlices(xics, bounds.StartIndex, bounds.EndIndex);
        if (slices is null)
        {
            return;
        }
        var polish = TukeyMedianPolish.Compute(slices.Value.PeakXics, slices.Value.PeakRts, 10, 0.01);
        if (polish != null)
        {
            _context.AddInfo(new MedianPolishByproduct(polish, slices.Value.PeakXics));
        }
    }

    /// <summary>Builds the (fragment index -> intensity slice) matrix + RT axis cropped to the peak window,
    /// the input to the Tukey median polish. Null when the window is too small or out of range.</summary>
    private static (List<KeyValuePair<int, double[]>> PeakXics, double[] PeakRts)? BuildPeakSlices(
        List<XicData> xics, int startIndex, int endIndex)
    {
        var peakLen = endIndex - startIndex + 1;
        if (peakLen < 3 || xics.Count == 0)
        {
            return null;
        }
        var scanCount = xics[0].RetentionTimes.Length;
        if (startIndex < 0 || endIndex >= scanCount)
        {
            return null;
        }

        var peakRts = new double[peakLen];
        for (var s = 0; s < peakLen; s++)
        {
            peakRts[s] = xics[0].RetentionTimes[startIndex + s];
        }
        var peakXics = new List<KeyValuePair<int, double[]>>(xics.Count);
        foreach (var xic in xics)
        {
            var slice = new double[peakLen];
            for (var s = 0; s < peakLen; s++)
            {
                slice[s] = xic.Intensities[startIndex + s];
            }
            peakXics.Add(new KeyValuePair<int, double[]>(xic.FragmentIndex, slice));
        }
        return (peakXics, peakRts);
    }

    /// <summary>Median-polish decomposition of one peak window, or null if the window/fit is degenerate.</summary>
    /// <summary>
    /// Runs the configured detector and converts its windows into the <see cref="XICPeakBounds"/> Osprey's
    /// feature calculators consume. RTs come from the shared grid (exactly what CWT itself stores), while
    /// Area / SignalToNoise / ApexIntensity are taken from the detector when it supplies them - Osprey's CWT
    /// does, so its path is unchanged - and derived from the chromatogram when it does not.
    /// </summary>
    private List<XICPeakBounds> DetectPeaks(List<XicData> xics, double minConsensusHeight, double? expectedRt)
    {
        var rts = xics[0].RetentionTimes;
        var windows = _detector.Detect(new PeakDetectionInput
        {
            FragmentIntensities = xics.Select(x => x.Intensities).ToList(),
            RetentionTimes = rts,
            ExpectedRt = expectedRt,
            MinConsensusHeight = minConsensusHeight,
        });

        var refIntensities = xics[ReferenceIndex(xics)].Intensities;
        var bounds = new List<XICPeakBounds>(windows.Count);
        foreach (var w in windows)
        {
            if (w.StartIndex < 0 || w.EndIndex >= rts.Length || w.EndIndex < w.StartIndex)
            {
                continue; // a detector must not hand back a window outside the grid; drop it rather than crash
            }
            bounds.Add(new XICPeakBounds
            {
                StartIndex = w.StartIndex,
                ApexIndex = w.ApexIndex,
                EndIndex = w.EndIndex,
                StartRt = RtAt(rts, w.StartIndex),
                ApexRt = RtAt(rts, w.ApexIndex),
                EndRt = RtAt(rts, w.EndIndex),
                ApexIntensity = w.ApexIntensity ?? ApexOf(refIntensities, w.ApexIndex),
                Area = w.Area ?? AreaOf(refIntensities, w.StartIndex, w.EndIndex),
                SignalToNoise = w.SignalToNoise ?? 0.0,
            });
        }
        return bounds;
    }

    private static double ApexOf(double[] intensities, int apexIndex) =>
        apexIndex >= 0 && apexIndex < intensities.Length ? intensities[apexIndex] : 0.0;

    private static double AreaOf(double[] intensities, int startIndex, int endIndex)
    {
        var area = 0.0;
        for (var i = Math.Max(0, startIndex); i <= Math.Min(intensities.Length - 1, endIndex); i++)
        {
            area += intensities[i];
        }
        return area;
    }

    /// <summary>Reference XIC = highest total intensity (>= last-on-tie, matching Osprey).</summary>
    private static int ReferenceIndex(List<XicData> xics)
    {
        var refIdx = 0;
        var refBest = -1.0;
        for (var f = 0; f < xics.Count; f++)
        {
            var total = 0.0;
            foreach (var v in xics[f].Intensities)
            {
                total += v;
            }
            if (total >= refBest)
            {
                refBest = total;
                refIdx = f;
            }
        }
        return refIdx;
    }

    private static int NearestIndex(double[] rts, double rt)
    {
        var best = 0;
        var bestD = double.MaxValue;
        for (var i = 0; i < rts.Length; i++)
        {
            var d = Math.Abs(rts[i] - rt);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The four pick-score terms over one window. Shared by <see cref="Repick"/> (CWT candidates)
    /// and <see cref="ScoreWindow"/> (a boundary reconciliation forced), so the two can never drift apart.</summary>
    private static (double Coelution, double LibCosine, double RtPenalty, double IntensityWeight, double Dt) WindowTerms(
        List<XicData> xics,
        List<LibraryFragment>? libFrags,
        double[] refIntensities,
        int startIndex,
        int endIndex,
        int apexIndex,
        double[] rts,
        double? expectedRt,
        double twoSigmaSq,
        double intensityExponent)
    {
        var sum = 0.0;
        var count = 0;
        for (var ii = 0; ii < xics.Count; ii++)
        {
            for (var jj = ii + 1; jj < xics.Count; jj++)
            {
                var corr = ScoringMath.PearsonCorrelationInRange(
                    xics[ii].Intensities, xics[jj].Intensities, startIndex, endIndex);
                if (!double.IsNaN(corr))
                {
                    sum += corr;
                    count++;
                }
            }
        }
        var coelution = count > 0 ? sum / count : 0.0;

        var dt = expectedRt.HasValue ? Math.Abs(RtAt(rts, apexIndex) - expectedRt.Value) : 0.0;
        var rtPenalty = expectedRt.HasValue ? Math.Exp(-(dt * dt) / twoSigmaSq) : 1.0;

        var apexIntensity = apexIndex >= 0 && apexIndex < refIntensities.Length ? refIntensities[apexIndex] : 0.0;
        // Intensity tiebreaker, ln(1+I)^w. Osprey uses w=1, but the other three terms are bounded on [0,1]
        // while this one is not, so at w=1 a far more intense interference can outvote co-elution, RT and
        // spectral match combined. w=0 drops the term (rank on evidence only).
        var intensityWeight = intensityExponent == 0.0
            ? 1.0
            : Math.Pow(Math.Log(1.0 + Math.Max(0.0, apexIntensity)), intensityExponent);

        // Library spectral-match term (median_polish_cosine over THIS window): a real peak matches the
        // predicted fragment pattern; a co-eluting interference does not. Neutral (1.0) when no library was
        // supplied or the polish did not converge, so it only ever adds discrimination.
        var libCosine = 1.0;
        if (libFrags is not null)
        {
            var polish = ComputePolish(xics, startIndex, endIndex);
            if (polish != null)
            {
                var lc = TukeyMedianPolish.LibCosine(polish, libFrags);
                if (!double.IsNaN(lc) && lc > 0.0)
                {
                    libCosine = lc;
                }
            }
        }

        return (coelution, libCosine, rtPenalty, intensityWeight, dt);
    }

    private static TukeyMedianPolishResult? ComputePolish(List<XicData> xics, int startIndex, int endIndex)
    {
        var slices = BuildPeakSlices(xics, startIndex, endIndex);
        return slices is null
            ? null
            : TukeyMedianPolish.Compute(slices.Value.PeakXics, slices.Value.PeakRts, 10, 0.01);
    }

    /// <summary>
    /// Aligns the library fragment intensities to the fragment XICs by product m/z (unit-resolution
    /// tolerance), yielding a per-XIC Osprey <see cref="LibraryFragment"/> vector in XIC order for
    /// <see cref="TukeyMedianPolish.LibCosine"/>. An unmatched XIC gets library intensity 0. Null when
    /// no library was supplied.
    /// </summary>
    private static List<LibraryFragment>? BuildAlignedLibFragments(
        List<double> xicProductMz, IReadOnlyList<CoreLibFragment>? library)
    {
        if (library is null || library.Count == 0)
        {
            return null;
        }
        const double mzTolerance = 0.05;
        var aligned = new List<LibraryFragment>(xicProductMz.Count);
        foreach (var mz in xicProductMz)
        {
            var bestDiff = double.MaxValue;
            var bestIntensity = 0f;
            foreach (var f in library)
            {
                var d = Math.Abs(f.Mz - mz);
                if (d < bestDiff)
                {
                    bestDiff = d;
                    bestIntensity = f.Intensity;
                }
            }
            aligned.Add(new LibraryFragment { Mz = mz, RelativeIntensity = bestDiff <= mzTolerance ? bestIntensity : 0f });
        }
        return aligned;
    }
}
