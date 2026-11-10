using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;

namespace FileMind;

/// <summary>Listens to the microphone's loudness only (nothing is stored), so a voice search can stop by itself
/// when you finish speaking.</summary>
public sealed class LevelMeter : IDisposable
{
    private AudioGraph? _graph;
    private AudioFrameOutputNode? _out;
    private AudioDeviceInputNode? _in;

    /// <summary>Loudness of the latest slice of sound, 0 to 1 (raised off the UI thread).</summary>
    public event Action<double>? Level;

    public async Task StartAsync()
    {
        var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech));
        if (created.Status != AudioGraphCreationStatus.Success) throw new InvalidOperationException($"Audio graph: {created.Status}");
        _graph = created.Graph;
        var input = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Speech);
        if (input.Status != AudioDeviceNodeCreationStatus.Success) { Dispose(); throw new InvalidOperationException($"Microphone: {input.Status}"); }
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
            if (node is null) return;
            using var frame = node.GetFrame();
            byte[] bytes;
            uint length;
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Read))
            {
                length = buffer.Length;
                if (length < 4) return;
                bytes = Windows.Storage.Streams.Buffer.CreateCopyFromMemoryBuffer(buffer).ToArray();
            }
            var n = (int)Math.Min(length, (uint)bytes.Length) / 4;      // 32-bit float samples
            if (n == 0) return;
            double sum = 0;
            for (var i = 0; i < n; i++) { var s = BitConverter.ToSingle(bytes, i * 4); sum += s * s; }
            Level?.Invoke(Math.Sqrt(sum / n));
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
