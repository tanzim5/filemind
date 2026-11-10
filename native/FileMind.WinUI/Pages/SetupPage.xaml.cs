using FileMind.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileMind.Pages;

/// <summary>Everything between launching the app and seeing the library: check the hardware, start the engine and make sure the AI model is downloaded.</summary>
public sealed partial class SetupPage : Page
{
    private TaskCompletionSource<bool>? _retry;
    private bool _forceCpu;
    private string? _lastAccel;

    public SetupPage() => InitializeComponent();

    public async Task RunAsync()
    {
        var rt = AppRuntime.Current;
        while (true)
        {
            try
            {
                rt.DetectHardware();
                if (_forceCpu) rt.ChooseEdition(EngineManifest.Cpu);

                var launch = rt.ResolveLaunch() ?? throw new InvalidOperationException(
                    "The engine folder is missing next to FileMind.exe. Unzip the whole download again and start File Mind from there.");

                ShowBusy("Starting File Mind", rt.SourceLabel is null ? "Waking the engine…" : $"Waking the engine ({rt.SourceLabel})…");
                await Task.Delay(300);   // let the setup page draw before the engine process is spawned
                await Task.Run(() => rt.StartEngineAsync(launch, CancellationToken.None));
                await EnsureModelAsync();
                return;
            }
            catch (Exception e)
            {
                App.Log("Setup", e, e.Message);
                await ShowErrorAsync(e.Message);
            }
        }
    }

    private async Task EnsureModelAsync()
    {
        var api = AppRuntime.Current.Api!;
        var started = false;
        while (true)
        {
            var m = await api.GetModelAsync();
            if (m.Ready) return;
            if (m.Error is { Length: > 0 }) throw new InvalidOperationException(m.Error);
            if (!m.Running)
            {
                if (started) throw new InvalidOperationException("The download finished, but the model is not ready. Please try again.");
                await api.StartModelDownloadAsync();
                started = true;
            }

            var what = m.Stage switch
            {
                "verify" => "Checking the AI engine…",
                "unpack" => "Unpacking the AI engine…",
                _ => m.File is { Length: > 0 } ? $"Downloading {m.File}" : "Downloading the AI model…",
            };
            _lastAccel = m.Accel;
            Headline.Text = "Getting File Mind ready";
            var runsOn = m.Accel == "cuda" ? "Runs on your NVIDIA graphics card." :
                         m.Accel == "cpu" ? $"Runs on the processor ({m.AccelReason})." : "";
            Detail.Text = $"{runsOn} This download happens once; after it, File Mind works fully offline.".Trim();
            ShowBusy(Headline.Text, what);
            SetBar(m.Done, m.Total);
            if (m.Total > 0) BusyText.Text = $"{what}\n{Mb(m.Done)} of {Mb(m.Total)}";
            await Task.Delay(700);
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        Headline.Text = "That didn't work";
        Detail.Text = "";
        BusyPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        var rt = AppRuntime.Current;
        UseCpuButton.Visibility = rt.SourceLabel is null && rt.Settings.Engine != "cpu"
            && (rt.Edition == EngineManifest.Nvidia || _lastAccel == "cuda") ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        _retry = new TaskCompletionSource<bool>();
        await _retry.Task;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowBusy(string headline, string text)
    {
        Headline.Text = headline;
        Detail.Text = "";
        ErrorPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Visible;
        BusyText.Text = text;
        Bar.IsIndeterminate = true;
    }

    private void SetBar(long done, long total)
    {
        if (total <= 0) { Bar.IsIndeterminate = true; return; }
        Bar.IsIndeterminate = false;
        Bar.Value = Math.Clamp((double)done / total, 0, 1);
    }

    private static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (1024.0 * 1024):0} MB";

    private void Retry_Click(object sender, RoutedEventArgs e) => _retry?.TrySetResult(true);

    private void UseCpu_Click(object sender, RoutedEventArgs e)
    {
        _forceCpu = true;
        _retry?.TrySetResult(true);
    }
}
