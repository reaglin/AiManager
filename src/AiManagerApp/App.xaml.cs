using System.IO;
using System.Windows;

namespace AiManagerApp;

public partial class App : Application
{
    private static readonly string CrashFile = Path.Combine(Path.GetTempPath(), "AiManager-crash.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            var root = args.Exception;
            while (root.InnerException is not null) root = root.InnerException;
            try
            {
                File.WriteAllText(CrashFile, args.Exception.ToString());
                Eaglin.AiManager.ActivityLog.Error("AiManagerApp", "Unhandled", args.Exception);
            }
            catch { /* best effort */ }
            MessageBox.Show($"Something went wrong:\n\n{root.Message}\n\nDetails were written to {CrashFile}",
                "AI Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
