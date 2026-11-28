using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FileMind.Core;

namespace FileMind;

/// <summary>Why listening could not work: "mic_denied", "no_mic", "model_missing", "busy", "starting" or "internal".</summary>
public sealed class SpeechException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record SpeechResult(string Text, Understood? Understood);

/// <summary>Talk to search. The microphone is streamed to the engine, which reads it with Whisper on this PC: the words
/// appear while you speak (<see cref="Partial"/>), and when you pause the final text comes back already understood
/// (dates, colours, sizes...). Nothing leaves the PC.</summary>
public sealed class SpeechInput : IDisposable
{
    private ClientWebSocket? _ws;
    private MicStream? _mic;
    private readonly SemaphoreSlim _send = new(1, 1);
    private volatile bool _cancelled;

    /// <summary>The words so far: the ones that are settled, and the ones that may still change (raised off the UI thread).</summary>
    public event Action<string, string>? Partial;
    /// <summary>True while a voice is heard.</summary>
    public event Action<bool>? Speech;
    /// <summary>Microphone loudness 0 to 1, for the pulsing ring.</summary>
    public event Action<double>? Level;

    public bool IsListening { get; private set; }

    /// <summary>Listens for one spoken question. Returns when the person pauses (or <see cref="Stop"/>); empty text when cancelled.</summary>
    public async Task<SpeechResult> ListenAsync()
    {
        if (IsListening) throw new SpeechException("busy", "Already listening.");
        var api = AppRuntime.Current.Api ?? throw new SpeechException("starting", "File Mind is still starting.");
        IsListening = true; _cancelled = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            _ws = new ClientWebSocket();
            try { await _ws.ConnectAsync(api.VoiceStreamUri, timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
            { throw new SpeechException("starting", "File Mind is still starting."); }
            var start = new Dictionary<string, object>(FileQuery.Clock()) { ["type"] = "start" };
            await SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(start)), WebSocketMessageType.Text, timeout.Token);

            _mic = new MicStream();
            _mic.Level += l => Level?.Invoke(l);
            _mic.Chunk += pcm => _ = SendAsync(pcm, WebSocketMessageType.Binary, CancellationToken.None);
            await _mic.StartAsync();

            var buffer = new byte[16 * 1024];
            while (!timeout.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, timeout.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return new SpeechResult("", null);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                } while (!r.EndOfMessage);

                using var doc = JsonDocument.Parse(sb.ToString());
                var root = doc.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "partial": Partial?.Invoke(root.GetProperty("stable").GetString() ?? "", root.GetProperty("tentative").GetString() ?? ""); break;
                    case "speech": Speech?.Invoke(root.GetProperty("on").GetBoolean()); break;
                    case "error": throw new SpeechException(root.GetProperty("code").GetString() ?? "internal", root.GetProperty("message").GetString() ?? "");
                    case "final":
                        var text = root.GetProperty("text").GetString() ?? "";
                        Understood? u = root.TryGetProperty("understood", out var uv) && uv.ValueKind == JsonValueKind.Object ? uv.Deserialize<Understood>() : null;
                        return new SpeechResult(text, u);
                }
            }
            return new SpeechResult("", null);
        }
        catch (OperationCanceledException) { return new SpeechResult("", null); }
        catch (WebSocketException) when (_cancelled) { return new SpeechResult("", null); }
        catch (WebSocketException) { throw new SpeechException("internal", "The voice connection dropped."); }
        finally
        {
            IsListening = false;
            try { _mic?.Dispose(); } catch (Exception) { }
            _mic = null;
            try { _ws?.Dispose(); } catch (Exception) { }
            _ws = null;
        }
    }

    /// <summary>Ends listening now; whatever was said so far is read and returned.</summary>
    public void Stop() => _ = SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"stop\"}"), WebSocketMessageType.Text, CancellationToken.None);

    /// <summary>Stops listening and throws the words away.</summary>
    public void Cancel()
    {
        _cancelled = true;
        _ = SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"cancel\"}"), WebSocketMessageType.Text, CancellationToken.None);
        try { _mic?.Dispose(); } catch (Exception) { }
    }

    private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        try
        {
            await _send.WaitAsync(ct);
            try { await ws.SendAsync(data, type, true, ct); } finally { _send.Release(); }
        }
        catch (Exception) { }
    }

    public void Dispose() { try { _mic?.Dispose(); _ws?.Dispose(); } catch (Exception) { } }
}
