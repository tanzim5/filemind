using Windows.Media.SpeechRecognition;

namespace FileMind;

/// <summary>Talk to search. Uses Windows' own speech recognizer, which streams words while you speak and stops by
/// itself when you pause. If that is unavailable (no speech language, privacy setting off) it records the microphone
/// and lets the engine transcribe it with local Whisper.</summary>
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
            if (!_windowsFailed)
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
        _fallbackStop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await Task.Delay(Timeout.Infinite, _fallbackStop.Token); } catch (TaskCanceledException) { }
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
