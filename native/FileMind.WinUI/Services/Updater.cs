using System.Diagnostics;
using FileMind.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileMind;

/// <summary>Finds out whether a newer File Mind exists and updates in the way that suits how this copy was installed.</summary>
public static class Updater
{
    public static string CurrentVersion => typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static InstallMode Mode => UpdateService.DetectMode(AppContext.BaseDirectory);
    /// <summary>A working copy used for development is never replaced.</summary>
    public static bool Available => AppRuntime.Current.SourceLabel is null;
    public static UpdateInfo? Pending { get; private set; }

    /// <summary>Asks GitHub for the latest release. Automatic checks happen at most once a day and only when the setting is on.</summary>
    public static async Task<UpdateInfo?> CheckAsync(bool manual)
    {
        var rt = AppRuntime.Current;
        if (!Available) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!manual && (!rt.Settings.CheckUpdates || now - rt.Settings.LastUpdateCheck < 86400)) return Pending;
        Pending = await UpdateService.CheckAsync(rt.Http, CurrentVersion);
        rt.Settings.LastUpdateCheck = now;
        rt.Settings.Save(rt.Paths);
        return Pending;
    }

    /// <summary>One notice at start-up when a newer version exists (not for a version the user chose to skip).</summary>
    public static async Task NotifyAsync(XamlRoot root)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            var info = await CheckAsync(false);
            var rt = AppRuntime.Current;
            if (info is null || info.Version == rt.Settings.SkipVersion) return;
            var how = Mode switch
            {
                InstallMode.Store => "The Microsoft Store will update it for you.",
                InstallMode.Winget => "Update it with winget.",
                _ => "File Mind can download and install it now. Your files and settings are kept.",
            };
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = $"File Mind {info.Version} is available", Content = $"You have {CurrentVersion}. {how}",
                PrimaryButtonText = Mode == InstallMode.Store ? "OK" : "Update", SecondaryButtonText = "Skip this version", CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary) { rt.Settings.SkipVersion = info.Version; rt.Settings.Save(rt.Paths); }
            else if (result == ContentDialogResult.Primary && Mode != InstallMode.Store) await InstallAsync(info, null);
        }
        catch (Exception ex) { App.Log("update-notify", ex, ex.Message); }
    }

    /// <summary>winget: opens a terminal running "winget upgrade". Zip: download, verify, swap the folder and restart. Store: nothing to do.</summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<double>? progress)
    {
        var rt = AppRuntime.Current;
        switch (Mode)
        {
            case InstallMode.Store: return;
            case InstallMode.Winget:
                Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoExit", "-Command", $"winget upgrade --id {UpdateService.WingetId}" }, UseShellExecute = true });
                return;
        }
        if (Mode == InstallMode.Msi && info.MsiUrl is not null)
        {
            try
            {
                var msi = await UpdateService.DownloadMsiAsync(rt.Http, info, Path.Combine(Path.GetTempPath(), "filemind-update"), progress, CancellationToken.None);
                UpdateService.RunInstallerAndRestart(msi, Environment.ProcessId);
                rt.Host.Stop();
                Application.Current.Exit();
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or HttpRequestException or IOException)
            {
                App.Log("update-msi", ex, ex.Message);
                Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true });
            }
            return;
        }
        try
        {
            var zip = await UpdateService.DownloadAsync(rt.Http, info, Path.Combine(Path.GetTempPath(), "filemind-update"), progress, CancellationToken.None);
            UpdateService.StageAndRestart(zip, AppContext.BaseDirectory, Environment.ProcessId);
            rt.Host.Stop();
            Application.Current.Exit();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or InvalidDataException or HttpRequestException)
        {
            App.Log("update-install", ex, ex.Message);
            // Could not update by itself (folder not writable, no checksum, no network): the release page has the zip.
            Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true });
        }
    }
}
