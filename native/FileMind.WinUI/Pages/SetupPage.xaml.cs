using FileMind.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileMind.Pages;

/// <summary>Everything between launching the app and seeing the library: check the hardware, install the right engine
/// once, start it, and make sure the AI model is downloaded.</summary>
public sealed partial class SetupPage : Page
{
    private TaskCompletionSource<string>? _choice;
    private TaskCompletionSource<bool>? _retry;
    private bool _forceCpu;

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

                var launch = rt.ResolveLaunch();
                if (launch is null)
                {
                    await ChooseAndInstallAsync();
                    launch = rt.ResolveLaunch() ?? throw new InvalidOperationException("The engine was installed but could not be found.");
                }

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

    private async Task ChooseAndInstallAsync()
    {
        var rt = AppRuntime.Current;
        var gpu = rt.Gpu;
        Headline.Text = "Choose your engine";
        Detail.Text = "File Mind downloads its engine once. Everything after that works offline.";
        GpuName.Text = gpu.Headline;
        GpuReason.Text = gpu.Reason;

        var nvidia = rt.Manifest.Editions[EngineManifest.Nvidia];
        var cpu = rt.Manifest.Editions[EngineManifest.Cpu];
        NvidiaNote.Text = gpu.CudaUsable
            ? $"Recommended for this PC. {Mb(nvidia.Size)} now, plus about 2.8 GB of GPU libraries with the AI model on first start."
            : "Not available on this PC.";
        CpuNote.Text = $"Works on any PC. {Mb(cpu.Size)}, slower indexing.";
        NvidiaOption.IsEnabled = gpu.CudaUsable;
        EditionChoice.SelectedItem = gpu.CudaUsable ? NvidiaOption : CpuOption;

        _choice = new TaskCompletionSource<string>();
        ChoicePanel.Visibility = Visibility.Visible;
        BusyPanel.Visibility = ErrorPanel.Visibility = Visibility.Collapsed;
        var picked = await _choice.Task;
        ChoicePanel.Visibility = Visibility.Collapsed;

        var auto = GpuProbe.ChooseEdition("auto", gpu);
        rt.ChooseEdition(picked == auto ? "auto" : picked);   // "auto" keeps following the hardware if it changes later

        var edition = rt.EditionInfo;
        ShowBusy($"Installing the {edition.Title} engine", "Starting the download…");
        var progress = new Progress<InstallProgress>(p =>
        {
            switch (p.Stage)
            {
                case "download":
                    SetBar(p.Done, p.Total);
                    BusyText.Text = $"Downloading · {Mb(p.Done)} of {Mb(p.Total)}";
                    break;
                case "verify":
                    SetBar(0, 0);
                    BusyText.Text = "Checking the download…";
                    break;
                case "unpack":
                    SetBar(p.Done, p.Total);
                    BusyText.Text = "Unpacking…";
                    break;
            }
        });
        await rt.Installer.InstallAsync(rt.Edition, edition, rt.Manifest.Version, progress, CancellationToken.None);
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
                "verify" => "Checking the GPU libraries…",
                "unpack" => "Unpacking the GPU libraries…",
                _ => m.File is { Length: > 0 } ? $"Downloading {m.File}" : "Downloading the AI model…",
            };
            Headline.Text = "Downloading the AI model";
            Detail.Text = "This happens once. After it, File Mind works fully offline.";
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
        ChoicePanel.Visibility = BusyPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        UseCpuButton.Visibility = AppRuntime.Current.Edition == EngineManifest.Nvidia && AppRuntime.Current.SourceLabel is null
            ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        _retry = new TaskCompletionSource<bool>();
        await _retry.Task;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowBusy(string headline, string text)
    {
        Headline.Text = headline;
        Detail.Text = "";
        ChoicePanel.Visibility = ErrorPanel.Visibility = Visibility.Collapsed;
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

    private void Install_Click(object sender, RoutedEventArgs e) =>
        _choice?.TrySetResult((EditionChoice.SelectedItem as RadioButton)?.Tag as string ?? EngineManifest.Cpu);

    private void Retry_Click(object sender, RoutedEventArgs e) => _retry?.TrySetResult(true);

    private void UseCpu_Click(object sender, RoutedEventArgs e)
    {
        _forceCpu = true;
        _retry?.TrySetResult(true);
    }
}
