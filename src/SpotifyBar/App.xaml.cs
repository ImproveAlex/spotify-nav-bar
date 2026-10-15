using System.IO;
using System.Security.Principal;
using System.Windows;

namespace SpotifyBar;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(Storage.Root);
                File.AppendAllText(Path.Combine(Storage.Root, "errors.log"),
                    $"{DateTimeOffset.UtcNow:u} {args.Exception.GetType().Name}\n{args.Exception.StackTrace}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show("Spotify Bar encountered an unexpected error and needs to close. Reopen it to try again.", "Spotify Bar", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        var smoke = e.Args.Contains("--smoke-test");
        if (!smoke)
        {
            var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            _instance = new Mutex(true, @"Local\SpotifyBar." + user, out var created);
            if (!created)
            {
                MessageBox.Show("Spotify Bar is already running at the top of your screen.", "Spotify Bar");
                _instance.Dispose();
                _instance = null;
                Shutdown();
                return;
            }
        }
        var demo = e.Args.Contains("--demo") || smoke;
        try { Storage.Initialize(migrateLegacy: !demo, createPreferences: !demo); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Spotify Bar needs write access to your Documents folder to save its settings and help files. Restore access and try again.",
                "Spotify Bar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        MainWindow = new MainWindow(demo, smoke);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
