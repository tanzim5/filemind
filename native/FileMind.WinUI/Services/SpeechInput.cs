using Windows.Media.SpeechRecognition;

namespace FileMind;

/// <summary>Talk to search. By default the microphone is recorded and the engine turns it into text with local
/// Whisper, so nothing leaves the PC; it stops by itself when you pause. Settings can switch to Windows' own speech
/// recognizer instead, which shows words while you speak but may use Microsoft's online service.</summary>
public sealed class SpeechInput : IDisposable
{
    private SpeechRecognizer? _recognizer;
    private bool _windowsFailed;
    private VoiceRecorder? _recorder;
    private CancellationTokenSource? _fallbackStop;

    /// <summary>Partial words while you are still speaking (raised off the UI thread).</summary>
    public event Action<string>? Hypothesis;

    public bool IsListening { get; private set; }

    /// <summary>Listens for one spoken query and returns its text ("" when nothing was heard).</summary>
    public async Task<string> ListenAsync()
    {
        IsListening = true;
        try
        {
            if (AppRuntime.Current.Settings.VoiceEngine == "windows" && !_windowsFailed)
            {
                try { return await ListenWindowsAsync(); }
                catch (Exception ex)
                {
                    App.Log("speech", ex, "Windows speech recognition unavailable; using local Whisper");
                    _windowsFailed = true;
                    _recognizer?.Dispose(); _recognizer = null;
                }
            }
            return await ListenWhisperAsync();
        }
        finally { IsListening = false; }
    }

    /// <summary>Ends listening early; whatever was said so far is used.</summary>
    public void Stop()
    {
        _fallbackStop?.Cancel();
        try { _ = _recognizer?.StopRecognitionAsync(); } catch (Exception) { }
    }

    private async Task<string> ListenWindowsAsync()
    {
        if (_recognizer is null)
        {
            var r = new SpeechRecognizer();
            r.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.WebSearch, "search"));
            var compiled = await r.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                r.Dispose();
                throw new InvalidOperationException($"Speech constraints: {compiled.Status}");
            }
            r.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(6);
            r.Timeouts.EndSilenceTimeout = TimeSpan.FromMilliseconds(700);
            r.HypothesisGenerated += (_, e) => Hypothesis?.Invoke(e.Hypothesis.Text);
            _recognizer = r;
        }
        var result = await _recognizer.RecognizeAsync();
        if (result.Status is SpeechRecognitionResultStatus.Success or SpeechRecognitionResultStatus.UserCanceled)
            return result.Text ?? "";
        throw new InvalidOperationException($"Speech recognition: {result.Status}");
    }

    private async Task<string> ListenWhisperAsync()
    {
        var api = AppRuntime.Current.Api ?? throw new InvalidOperationException("File Mind is still starting.");
        _recorder ??= new VoiceRecorder();
        await _recorder.StartAsync();
        Hypothesis?.Invoke("Listening…");
        _ = api.WarmVoiceAsync();                       // the model loads while you are still speaking
        _fallbackStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stop = _fallbackStop;
        LevelMeter? meter = null;
        try
        {
            // Stop by itself: once speech has been heard, 0.9 s of quiet ends it; 6 s with no speech at all gives up.
            meter = new LevelMeter();
            var started = DateTime.UtcNow;
            DateTime? lastLoud = null;
            var floor = 0.004;
            meter.Level += level =>
            {
                var now = DateTime.UtcNow;
                if ((now - started).TotalMilliseconds < 250) { floor = Math.Max(floor, level * 1.2); return; }   // room noise
                if (level > Math.Max(floor * 2.5, 0.012)) lastLoud = now;
                var quiet = lastLoud is null ? (now - started).TotalSeconds > 6 : (now - lastLoud.Value).TotalMilliseconds > 900;
                if (quiet) { try { stop.Cancel(); } catch (Exception) { } }
            };
            await meter.StartAsync();
        }
        catch (Exception ex)
        {
            App.Log("speech", ex, "no level meter; listening for a fixed 8 seconds");
            meter?.Dispose(); meter = null;
            stop.CancelAfter(TimeSpan.FromSeconds(8));
        }
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (TaskCanceledException) { }
        meter?.Dispose();
        var path = await _recorder.StopAsync();
        if (path is null) return "";
        try
        {
            Hypothesis?.Invoke("Understanding…");
            return await api.VoiceToTextAsync(path);
        }
        finally { try { File.Delete(path); } catch (Exception) { } }
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recorder?.Dispose();
    }
}
