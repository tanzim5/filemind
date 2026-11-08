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
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FileMind.Native.Show");
        if (!first) { _showSignal.Set(); FocusRunningInstance(); Environment.Exit(0); return; }
        try
        {
            Window = new MainWindow();
            if (!Environment.GetCommandLineArgs().Contains("--hidden")) Window.Activate();
            StartLauncher();
            // A second launch (double-clicking FileMind.exe while it sits in the tray) brings the main window back.
            ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Window.DispatcherQueue.TryEnqueue(ShowMain), null, -1, false);
        }
        catch (Exception ex) { Log("launch", ex, ex.Message); throw; }
    }

    public static QuickSearch.QuickSearchWindow? Launcher { get; private set; }
    private static NativeHost? _native;
    private static EventWaitHandle? _showSignal;
    public static bool Quitting { get; private set; }
    /// <summary>The shortcut actually registered (null when every candidate was taken).</summary>
    public static string? ActiveHotkey { get; private set; }

    /// <summary>The quick-search launcher, its global shortcut and the tray icon. The launcher window is created now and
    /// only shown/hidden afterwards, so the shortcut opens it instantly.</summary>
    private static void StartLauncher()
    {
        try
        {
            Launcher = new QuickSearch.QuickSearchWindow();
            Launcher.AppWindow.Hide();
            _native = new NativeHost(Launcher.Hwnd);
            _native.HotkeyPressed += () => Launcher.DispatcherQueue.TryEnqueue(Launcher.Toggle);
            _native.OpenRequested += () => Window?.DispatcherQueue.TryEnqueue(ShowMain);
            _native.QuitRequested += () => Window?.DispatcherQueue.TryEnqueue(Quit);
            // The chosen shortcut first; if another app owns it, the first free fallback (not saved, so it is retried next time).
            var wanted = AppRuntime.Current.Settings.QuickSearchHotkey;
            ActiveHotkey = new[] { wanted, "Ctrl+Shift+Space", "Ctrl+Alt+Shift+Space", "Alt+Shift+F" }.Distinct()
                .FirstOrDefault(c => _native.RegisterHotkey(c));
            if (ActiveHotkey != wanted) Log("hotkey", null, $"{wanted} is used by another app; using {ActiveHotkey ?? "none"}");
            // Development aid: FILEMIND_DEV_LAUNCHER=<text> opens the launcher with that query once the engine is up.
            if (Environment.GetEnvironmentVariable("FILEMIND_DEV_LAUNCHER") is { Length: > 0 } dev)
            {
                var t = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                t.Tick += (_, _) => { t.Stop(); Launcher.Toggle(); Launcher.DevQuery(dev); };
                t.Start();
            }
            _native.ShowTrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "filemind.ico"),
                ActiveHotkey is null ? "File Mind" : $"File Mind — {ActiveHotkey} to search");
        }
        catch (Exception ex) { Log("launcher", ex, ex.Message); }
    }

    /// <summary>Re-registers the shortcut after it was changed in Settings. False when another app owns it.</summary>
    public static bool SetHotkey(string combo)
    {
        var ok = _native?.RegisterHotkey(combo) ?? false;
        if (ok) ActiveHotkey = combo;
        return ok;
    }

    public static void ShowMain()
    {
        if (Window is null) return;
        Window.AppWindow.Show();
        Window.Activate();
    }

    /// <summary>Really exit: tray menu "Quit", or closing the window when "keep running" is off.</summary>
    public static void Quit()
    {
        Quitting = true;
        try { _native?.Dispose(); } catch (Exception) { }
        AppRuntime.Current.Shutdown();
        Environment.Exit(0);
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
