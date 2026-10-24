using System.Windows;
using CoDDedupe.Core;
using Spock.AppKit;

namespace CoDDedupe;

public partial class App : Application
{
    /// <summary>"--tray": start hidden in the tray (for Start with Windows).</summary>
    public static bool StartInTray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashGuard.Install(this, "CoD Dedupe", Settings.LogFolder);

        // "CoDDedupe.exe --share" and friends: no window, just do it and report through the console or a log file.
        if (CliRun.Wanted(e.Args))
        {
            int code;
            using (var log = CliLog.Open(Settings.LogFolder, "cli")) code = CliRun.Run(e.Args, log);
            Shutdown(code);
            return;
        }

        StartInTray = Array.IndexOf(e.Args, "--tray") >= 0;

        // A documentation screenshot should show the app, not which store apps happen to be open right now.
        if (Array.IndexOf(e.Args, "--screenshot") >= 0) Launchers.Override = () => new List<string>();

        var settings = Settings.Load();
        Theme.Apply(Theme.FromArgsOrSetting(e.Args, settings.Theme));

        var window = new MainWindow();
        if (!StartInTray || !window.CanHideToTray) window.Show();
        else window.HideToTray(firstTime: false);

        // Hidden helper for documentation and the CI smoke test: CoDDedupe.exe --screenshot out.png
        int i = Array.IndexOf(e.Args, "--screenshot");
        if (i >= 0 && i + 1 < e.Args.Length)
        {
            string target = e.Args[i + 1];
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(15000);   // let the install scan finish so the numbers show
                Shell.SaveScreenshot(window, target);
                window.ExitApp();
            };
        }
    }
}
