using System.Windows;

namespace AppleSniff;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Write($"--- AppleSniff started ({Environment.OSVersion})");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI error", args.Exception);
            args.Handled = true;
            MessageBox.Show("Something went wrong:\n\n" + args.Exception.Message + "\n\nDetails were saved to:\n" + Log.FilePath,
                "AppleSniff", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("Fatal error", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Background error", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }
}
