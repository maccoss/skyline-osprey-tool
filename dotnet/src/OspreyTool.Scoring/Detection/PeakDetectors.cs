using OspreyTool.Core.Detection;

namespace OspreyTool.Scoring.Detection;

/// <summary>
/// The registry of available peak detectors - what the Settings dropdown lists and what <c>--detector</c>
/// resolves. To add an algorithm: implement <see cref="IPeakDetector"/> (no Osprey dependency needed - see
/// <see cref="LocalMaximaPeakDetector"/>) and <see cref="Register"/> it, or pass the instance straight to
/// <see cref="OspreyFeatureScorer"/>'s constructor.
/// </summary>
public static class PeakDetectors
{
    private static readonly List<IPeakDetector> Registered = new()
    {
        new OspreyCwtPeakDetector(),
        new LocalMaximaPeakDetector(),
    };

    private static readonly object Gate = new();

    /// <summary>Osprey CWT - what the tool uses unless told otherwise.</summary>
    public static IPeakDetector Default => Registered[0];

    public static IReadOnlyList<IPeakDetector> All
    {
        get
        {
            lock (Gate)
            {
                return Registered.ToList();
            }
        }
    }

    /// <summary>Adds a detector (replacing any with the same <see cref="IPeakDetector.Id"/>).</summary>
    public static void Register(IPeakDetector detector)
    {
        lock (Gate)
        {
            Registered.RemoveAll(d => d.Id.Equals(detector.Id, StringComparison.OrdinalIgnoreCase));
            Registered.Add(detector);
        }
    }

    /// <summary>Resolves by <see cref="IPeakDetector.Id"/>; null when unknown (callers report the valid ids).</summary>
    public static IPeakDetector? ById(string id)
    {
        lock (Gate)
        {
            return Registered.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static string Ids => string.Join(" | ", All.Select(d => d.Id));
}
