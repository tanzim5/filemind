using Microsoft.UI.Xaml;

namespace FileMind;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
        // Anything that escapes is written next to the engine log, so a crash is never a mystery.
        // Logged and swallowed: a failed click or a bad value must never close the app.
        UnhandledException += (_, e) => { Log("UI", e.Exception, e.Message); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Domain", e.ExceptionObject as Exception, null);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("Task", e.Exception, null); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One window per user: a second launch just brings the first one forward.
        _single = new Mutex(true, @"Local\FileMind.Native.Main", out var first);
        if (!first) { FocusRunningInstance(); Environment.Exit(0); return; }
        try { Window = new MainWindow(); Window.Activate(); }
        catch (Exception ex) { Log("launch", ex, ex.Message); throw; }
    }

    private static Mutex? _single;

    private static void FocusRunningInstance()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("FileMind"))
                if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    internal static void Trace(string message) => Log("trace", null, message);

    internal static void Log(string where, Exception? ex, string? message)
    {
        try
        {
            var dir = new FileMind.Core.AppPaths().Logs;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"), $"--- {DateTime.Now:u} [{where}] {message}\n{ex}\n");
        }
        catch { }
    }
}
