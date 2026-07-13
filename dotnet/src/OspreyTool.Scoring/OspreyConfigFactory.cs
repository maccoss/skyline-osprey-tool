using OspreyTool.Core;
using pwiz.Osprey.Core;

namespace OspreyTool.Scoring;

/// <summary>
/// Builds an <see cref="OspreyConfig"/> from the Skyline document's transition settings so
/// Osprey's fragment matching uses the SAME tolerances Skyline extracted the chromatograms with,
/// rather than a hardcoded value. (For the XIC-only 12-feature subset the tolerance is not yet
/// read by any active calculator - it matters once apex-spectrum features are added - but setting
/// it from the document keeps the config correct as that surface grows.)
/// </summary>
public static class OspreyConfigFactory
{
    public static OspreyConfig FromTransitionSettings(SkylineTransitionSettings settings)
    {
        var config = new OspreyConfig();
        if (settings.IsUnitResolution)
        {
            config.ResolutionMode = ResolutionMode.UnitResolution;
            config.FragmentTolerance = FragmentToleranceConfig.UnitResolution(settings.EffectiveProductTolerance);
            config.PrecursorTolerance = FragmentToleranceConfig.UnitResolution(settings.EffectivePrecursorTolerance);
        }
        else
        {
            // High-res analyzers: Skyline's "res" is resolving power, not ppm. Left as a ppm default
            // until HRAM PRM is actually supported (M0 target is unit-resolution Stellar).
            config.ResolutionMode = ResolutionMode.HRAM;
            config.FragmentTolerance = FragmentToleranceConfig.Hram(20.0);
            config.PrecursorTolerance = FragmentToleranceConfig.Hram(20.0);
        }
        return config;
    }
}
