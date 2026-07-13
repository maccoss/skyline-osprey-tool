using System.Windows;
using OspreyTool.Skyline;

namespace OspreyTool.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

    private static void ShowFatal(Exception ex) => MessageBox.Show(
        ex.ToString(), "Osprey Tool - startup error", MessageBoxButton.OK, MessageBoxImage.Error);
}
