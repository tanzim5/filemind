using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Capture;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Render;

namespace FileMind;

/// <summary>The microphone as a stream of 16 kHz, mono, 16-bit sound in 100 ms pieces, plus its loudness. Nothing is stored.</summary>
public sealed class MicStream : IDisposable
{
    private const int Rate = 16000;
    private AudioGraph? _graph;
    private AudioFrameOutputNode? _out;
    private AudioDeviceInputNode? _in;
    private double _phase;
    private readonly List<short> _pending = [];

    /// <summary>A 100 ms piece of little-endian int16 mono 16 kHz sound (raised off the UI thread).</summary>
    public event Action<byte[]>? Chunk;
    /// <summary>Loudness of the latest slice, 0 to 1.</summary>
    public event Action<double>? Level;

    /// <summary>Starts listening. Throws <see cref="SpeechException"/> ("mic_denied" or "no_mic") when that is not possible.</summary>
    public async Task StartAsync()
    {
        var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
        if (created.Status != AudioGraphCreationStatus.Success)
            throw new SpeechException("no_mic", $"No microphone could be opened ({created.Status}).");
        _graph = created.Graph;
        var input = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Speech);
        if (input.Status != AudioDeviceNodeCreationStatus.Success)
        {
            var denied = input.Status == AudioDeviceNodeCreationStatus.AccessDenied;
            Dispose();
            throw new SpeechException(denied ? "mic_denied" : "no_mic", denied ? "Microphone access is off for desktop apps." : $"No microphone found ({input.Status}).");
        }
        _in = input.DeviceInputNode;
        _out = _graph.CreateFrameOutputNode();
        _in.AddOutgoingConnection(_out);
        _graph.QuantumStarted += OnQuantum;
        _graph.Start();
    }

    private void OnQuantum(AudioGraph sender, object args)
    {
        try
        {
            var node = _out;
            if (node is null || _graph is null) return;
            using var frame = node.GetFrame();
            byte[] bytes;
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Read))
            {
                if (buffer.Length < 4) return;
                bytes = Windows.Storage.Streams.Buffer.CreateCopyFromMemoryBuffer(buffer).ToArray();
            }
            var channels = Math.Max(1, (int)_graph.EncodingProperties.ChannelCount);
            var rate = Math.Max(8000, (int)_graph.EncodingProperties.SampleRate);
            var frames = bytes.Length / 4 / channels;                   // 32-bit float samples, channels interleaved
            if (frames == 0) return;
            var mono = new float[frames];
            double sum = 0;
            for (var i = 0; i < frames; i++)
            {
                float m = 0;
                for (var c = 0; c < channels; c++) m += BitConverter.ToSingle(bytes, (i * channels + c) * 4);
                mono[i] = m / channels; sum += mono[i] * mono[i];
            }
            Level?.Invoke(Math.Sqrt(sum / frames));

            var step = rate / (double)Rate;                              // average every `step` samples into one 16 kHz sample
            for (; _phase + step <= frames; _phase += step)
            {
                int from = (int)Math.Floor(_phase), to = Math.Min(frames, (int)Math.Floor(_phase + step));
                float acc = 0; var count = Math.Max(1, to - from);
                for (var i = from; i < from + count && i < frames; i++) acc += mono[i];
                _pending.Add((short)Math.Clamp((int)(acc / count * 32767f), short.MinValue, short.MaxValue));
            }
            _phase -= frames;
            if (_pending.Count >= Rate / 10)
            {
                var pcm = new byte[_pending.Count * 2];
                Buffer.BlockCopy(_pending.ToArray(), 0, pcm, 0, pcm.Length);
                _pending.Clear();
                Chunk?.Invoke(pcm);
            }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        try { if (_graph is not null) { _graph.QuantumStarted -= OnQuantum; _graph.Stop(); } } catch (Exception) { }
        try { _out?.Dispose(); _in?.Dispose(); _graph?.Dispose(); } catch (Exception) { }
        _out = null; _in = null; _graph = null;
    }
}
