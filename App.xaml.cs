namespace EncryptedDiary.WinUI;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window ??= new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogStartupException(ex);
            ShowFatalMessage(
                "Kiary failed to start.",
                $"Startup exception logged to:{Environment.NewLine}{GetStartupLogPath()}");
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            LogStartupException(e.Exception);
            e.Handled = true;
        }
        catch
        {
            // best-effort logging only
        }
    }

    private static void LogStartupException(Exception exception)
    {
        string logPath = GetStartupLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(
            logPath,
            $"{DateTimeOffset.Now:u}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}Inner:{Environment.NewLine}{exception.InnerException}");
    }

    private static string GetStartupLogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kiary",
            "startup-error.log");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private static void ShowFatalMessage(string caption, string text)
    {
        _ = MessageBoxW(nint.Zero, text, caption, 0x00000010U);
    }
}
