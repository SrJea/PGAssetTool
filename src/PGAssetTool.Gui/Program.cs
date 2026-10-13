using Avalonia;

namespace PGAssetTool.Gui;

internal static class Program
{
    // Avalonia has to be initialised before anything touches its types, so this stays free of
    // everything but the builder.
    [STAThread]
    public static int Main(string[] args)
    {
        // A window cannot be looked at from a build, so --self-test brings the app up, waits for
        // the game to load, reports what the model holds and exits. It is the only way to know the
        // startup path works without a person sitting in front of it.
        //
        // --game points it at an installation of its own choosing. It writes to whatever game it
        // opens, and with nothing said that is the one Steam knows about — the one being played.
        if (args.Contains("--self-test"))
        {
            var at = Array.IndexOf(args, "--game");
            return SelfTest.Run(at >= 0 && at + 1 < args.Length ? args[at + 1] : null);
        }

        // Whatever gets this far takes the window down, and until now took with it everything that
        // said why. The report is what somebody can be asked to send; the next start points at it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Core.Settings.ErrorLog.Crash(
                e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()),
                "an error nothing caught");

        // A task nobody awaited fails without a word. It does not take the window down, so it is
        // only written down.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Core.Settings.ErrorLog.Record(e.Exception, "a background task nobody waited for");
            e.SetObserved();
        };

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .WithInterFont()
            .LogToTrace();
}
