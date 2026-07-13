using OspreyTool.Core.Detection;
using pwiz.Osprey.Chromatography;
using pwiz.Osprey.Core;

namespace OspreyTool.Scoring.Detection;

/// <summary>
/// The default detector: Osprey's continuous-wavelet-transform consensus peak detection
/// (<see cref="CwtPeakDetector.DetectConsensusPeaks"/>), wrapped behind <see cref="IPeakDetector"/>.
///
/// CWT's own Area / SignalToNoise / ApexIntensity are carried through on the <see cref="PeakWindow"/>,
/// because Osprey's feature calculators read them - deriving them again here would silently change the
/// feature vector (and so the FDR) relative to stock Osprey.
/// </summary>
public sealed class OspreyCwtPeakDetector : IPeakDetector
{
    public string Id => "osprey-cwt";

    public string DisplayName => "Osprey CWT (default)";

    public IReadOnlyList<PeakWindow> Detect(PeakDetectionInput input)
    {
        if (input.FragmentCount < 2)
        {
            return Array.Empty<PeakWindow>();
        }

        var xics = new List<XicData>(input.FragmentCount);
        for (var i = 0; i < input.FragmentCount; i++)
        {
            xics.Add(new XicData(i, input.RetentionTimes, input.FragmentIntensities[i]));
        }

        var peaks = CwtPeakDetector.DetectConsensusPeaks(xics, input.MinConsensusHeight);
        var windows = new List<PeakWindow>(peaks.Count);
        foreach (var p in peaks)
        {
            windows.Add(new PeakWindow(p.StartIndex, p.ApexIndex, p.EndIndex)
            {
                Area = p.Area,
                SignalToNoise = p.SignalToNoise,
                ApexIntensity = p.ApexIntensity,
            });
        }
        return windows;
    }
}
