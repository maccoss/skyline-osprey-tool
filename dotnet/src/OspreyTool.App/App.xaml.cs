using System.Windows;
using OspreyTool.Core;
using OspreyTool.Scoring;
using OspreyTool.Scoring.Detection;
using OspreyTool.Skyline;

namespace OspreyTool.App;

public partial class App : Application
{
    /// <summary>Non-interactive dependency check used by the packaging ship gate (build/verify-tool.ps1).</summary>
    public const string SelfCheckArg = "--self-check";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains(SelfCheckArg))
        {
            Shutdown(RunSelfCheck());
            return;
        }

        // Skyline launches the tool as a detached process with no console, so an unhandled exception here
        // just makes the tool "not start" with nothing to look at. Always surface it.
        DispatcherUnhandledException += (_, args) =>
        {
            ShowFatal(args.Exception);
            args.Handled = true;
        };

        // Skyline launches the tool with Arguments=$(SkylineConnection); build a connect-per-call session
        // from it (null when launched standalone -> the window falls back to manual file paths).
        ISkylineExecutor? session = null;
        try
        {
            session = SkylineSession.FromArguments(e.Args);
        }
        catch
        {
            session = null;
        }

        try
        {
            new MainWindow(session).Show();
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Exercises every assembly the tool depends on and actually RUNS the engine once on synthetic data.
    ///
    /// A plain launch is not enough of a check: .NET loads assemblies lazily, so a zip missing (say)
    /// OspreyTool.Scoring.dll or a native SQLite/SkiaSharp binary still opens its window happily and only
    /// dies when the user presses Run. This forces the whole graph to load - Core, Scoring + the Osprey
    /// engine DLLs, the Skyline RPC seam, and ScottPlot - so a broken package fails in CI, not on a user.
    ///
    /// Prints one line and returns an exit code; no window, no Skyline connection needed.
    /// </summary>
    private static int RunSelfCheck()
    {
        try
        {
            // Scoring + the Osprey engine: detect and score a peak on a synthetic chromatogram.
            var rts = new double[40];
            var frags = new List<XicData>();
            for (var f = 0; f < 3; f++)
            {
                var y = new double[40];
                for (var i = 0; i < 40; i++)
                {
                    rts[i] = 7.0 + i * 0.02;
                    var d = i - 20;
                    y[i] = (3 - f) * 1000.0 * Math.Exp(-(d * d) / 8.0);
                }
                frags.Add(new XicData
                {
                    FragmentIndex = f, RetentionTimes = rts, Intensities = y,
                    FragmentIon = $"y{f}", ProductMz = 100.0 * (f + 1),
                });
            }
            var result = OspreyFeatureScorer.CreateDefault().Repick(frags, 7.4, 0.0, 0.3);
            if (!result.HasPeak)
            {
                Console.Error.WriteLine("SELF-CHECK FAILED: the engine found no peak in the synthetic chromatogram.");
                return 1;
            }
            if (PeakDetectors.All.Count == 0)
            {
                Console.Error.WriteLine("SELF-CHECK FAILED: no peak detectors registered.");
                return 1;
            }

            // The Skyline RPC seam and the plotting stack (native SkiaSharp) must load too.
            _ = SkylineColorScheme.ClassicTransitions.Count;
            _ = new ScottPlot.Plot().GetImageBytes(80, 60);

            Console.WriteLine($"SELF-CHECK OK: engine picked a peak at {result.ApexRt:F2} min; " +
                $"{PeakDetectors.All.Count} detector(s); version " +
                $"{typeof(App).Assembly.GetName().Version}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SELF-CHECK FAILED: " + ex);
            return 1;
        }
    }

    private static void ShowFatal(Exception ex) => MessageBox.Show(
        ex.ToString(), "Osprey Tool - startup error", MessageBoxButton.OK, MessageBoxImage.Error);
}
