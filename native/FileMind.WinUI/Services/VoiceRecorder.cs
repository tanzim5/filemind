using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace FileMind;

/// <summary>Records the microphone with Windows' own MediaCapture into a small m4a. The engine turns the sound itself into the search.</summary>
public sealed class VoiceRecorder : IDisposable
{
    private MediaCapture? _capture;
    private LowLagMediaRecording? _recording;
    private StorageFile? _file;

    public bool IsRecording => _recording is not null;

    /// <summary>Starts listening. Throws when there is no microphone or access is switched off in Windows privacy settings.</summary>
    public async Task StartAsync()
    {
        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Audio, MediaCategory = MediaCategory.Speech });
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
        _file = await folder.CreateFileAsync($"filemind_voice_{DateTime.Now.Ticks}.m4a", CreationCollisionOption.GenerateUniqueName);
        _recording = await capture.PrepareLowLagRecordToStorageFileAsync(MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium), _file);
        await _recording.StartAsync();
        _capture = capture;
    }

    /// <summary>Stops and returns the recording's path, or null when nothing was captured.</summary>
    public async Task<string?> StopAsync()
    {
        var rec = _recording; _recording = null;
        if (rec is null) return null;
        try { await rec.StopAsync(); await rec.FinishAsync(); }
        finally { _capture?.Dispose(); _capture = null; }
        return _file?.Path;
    }

    public void Dispose()
    {
        try { _capture?.Dispose(); } catch (Exception) { }
        _capture = null; _recording = null;
    }
}
