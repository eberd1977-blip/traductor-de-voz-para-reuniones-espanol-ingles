using System.Net.WebSockets;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Providers;

namespace TraductorVozTeams.App.Providers;

public sealed class RealtimeTranslateFactory(Func<Uri, string, CancellationToken, Task<WebSocket>>? connect = null) : ITranslationProviderFactory
{
    private static readonly HttpMessageInvoker Http = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    public string ProviderType => RealtimeTranslateDescriptor.Type;

    public ITranslationProvider Create(ProviderSessionContext context) => new RealtimeTranslateProvider(context, connect ?? ConnectAsync);

    public async Task<ConnectionTestResult> TestConnectionAsync(ProviderSessionContext context, CancellationToken cancellationToken)
    {
        await using var provider = Create(context with { RealtimeTarget = "en" });
        string? failure = null;
        provider.Error += (_, error) => Volatile.Write(ref failure, error.Message);
        try
        {
            await provider.StartAsync(cancellationToken);
            await provider.StopAsync();
            return Volatile.Read(ref failure) is { } error
                ? new(false, error)
                : new(true, "Foundry confirmó la sesión de traducción. No se envió audio; verifica la voz por separado.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return new(false, RealtimeTranslateProvider.SafeError(ex, "No se pudo configurar la sesión"));
        }
    }

    private static async Task<WebSocket> ConnectAsync(Uri uri, string key, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("api-key", key);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(uri, Http, token);
            return socket;
        }
        catch (WebSocketException) when ((int)socket.HttpStatusCode is 401 or 403 or 404 or 429)
        {
            var status = (int)socket.HttpStatusCode;
            socket.Dispose();
            throw new RealtimeProtocolException(status switch
            {
                401 or 403 => "Foundry rechazó la credencial o el acceso al recurso.",
                404 => "No se encontró el endpoint o el despliegue de traducción.",
                _ => "Foundry alcanzó un límite de cuota o de sesiones."
            });
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class RealtimeTranslateProvider(
    ProviderSessionContext context,
    Func<Uri, string, CancellationToken, Task<WebSocket>> connect) : ITranslationProvider
{
    public static PcmFormat TranslationPcm { get; } = new(24000, 16, 1);
    public PcmFormat InputFormat => TranslationPcm;
    public PcmFormat OutputFormat => TranslationPcm;
    public event EventHandler<TranslationTextEventArgs>? Recognizing { add { } remove { } }
    public event EventHandler<TranslationTextEventArgs>? Recognized;
    public event EventHandler<TranslatedAudioEventArgs>? AudioReceived;
    public event EventHandler<ProviderErrorEventArgs>? Error;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private WebSocket? _socket;
    private Task? _sender;
    private Task? _receiver;
    private int _stopping;
    private int _failed;
    private int _disposed;
    private string? _transcriptEvent;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = new RealtimeTranslateDescriptor().Validate(context.Values, context.Secrets.Keys.ToHashSet());
        var target = context.RealtimeTarget;
        if (errors.Count != 0 || target is not ("en" or "es")
            || !context.Secrets.TryGetValue(RealtimeTranslateDescriptor.ApiKey, out var key) || string.IsNullOrWhiteSpace(key))
            throw new RealtimeProtocolException("Revisa el endpoint, despliegue, clave y destino de GPT.");

        var uri = new UriBuilder(RealtimeTranslateDescriptor.ResourceOrigin(context.Values)!)
        {
            Scheme = "wss", Port = -1, Path = "/openai/v1/realtime/translations",
            Query = "model=" + Uri.EscapeDataString(ProviderValues.Get(context.Values, RealtimeTranslateDescriptor.DeploymentKey))
        }.Uri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            _socket = await connect(uri, key, timeout.Token).ConfigureAwait(false);
            await SendAsync(new { type = "session.update", session = new { audio = new { output = new { language = target } } } }, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                using var message = await ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (EventType(message.RootElement) == "session.updated")
                    break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            throw new RealtimeProtocolException(SafeError(ex, "No se pudo iniciar GPT"));
        }
        _sender = SendAudioAsync();
        _receiver = ReceiveOutputAsync();
    }

    public void PushAudio(byte[] buffer, int count)
    {
        if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _failed) != 0 || count == 0)
            return;
        if (count < 0 || count > buffer.Length || count % InputFormat.BlockAlign != 0)
        {
            Fail("Se recibió un bloque PCM de captura inválido.");
            return;
        }
        // Split large callbacks so that the queue bounds both buffered time and memory.
        for (var offset = 0; offset < count; offset += 4800)
        {
            if (!_audio.Writer.TryWrite(buffer.AsSpan(offset, Math.Min(4800, count - offset)).ToArray()))
            {
                if (Volatile.Read(ref _stopping) == 0)
                    Fail("GPT no pudo enviar el audio a tiempo; se detuvo la sesión para evitar acumular demora.");
                break;
            }
        }
    }

    private async Task SendAudioAsync()
    {
        try
        {
            await foreach (var pcm in _audio.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                await SendAsync(new { type = "session.input_audio_buffer.append", audio = Convert.ToBase64String(pcm) }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
                Fail(SafeError(ex, "Se interrumpió el envío de audio a GPT"));
        }
    }

    private async Task ReceiveOutputAsync()
    {
        try
        {
            while (true)
            {
                using var message = await ReceiveAsync(_lifetime.Token).ConfigureAwait(false);
                var root = message.RootElement;
                var type = EventType(root);
                switch (type)
                {
                    case "session.output_audio.delta":
                        var data = root.GetProperty("delta").GetBytesFromBase64();
                        if (data.Length == 0 || data.Length % OutputFormat.BlockAlign != 0)
                            throw new RealtimeProtocolException("Foundry devolvió un bloque PCM traducido inválido.");
                        AudioReceived?.Invoke(this, new(new AudioChunk(data, OutputFormat)));
                        break;
                    case "session.output_transcript.delta":
                    case "response.text.delta":
                        _transcriptEvent ??= type;
                        if (_transcriptEvent == type)
                        {
                            var text = root.GetProperty(type == "response.text.delta" ? "text" : "delta").GetString();
                            if (!string.IsNullOrEmpty(text))
                                Recognized?.Invoke(this, new(string.Empty, text, null, null) { IsContinuousDelta = true });
                        }
                        break;
                    case "session.closed":
                        if (Volatile.Read(ref _stopping) == 0)
                            Fail("Foundry finalizó la sesión de traducción.");
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or JsonException
                                   or FormatException or KeyNotFoundException or OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
                Fail(SafeError(ex, "Se interrumpió la recepción de GPT"));
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;
        _audio.Writer.TryComplete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (_sender is not null)
                await _sender.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (_socket?.State == WebSocketState.Open && _receiver is not null && !_lifetime.IsCancellationRequested)
            {
                await SendAsync(new { type = "session.close" }, timeout.Token).ConfigureAwait(false);
                await _receiver.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (!_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open)
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or InvalidOperationException)
        {
            Fail(SafeError(ex, "GPT no confirmó el cierre"));
        }
        finally
        {
            _lifetime.Cancel();
            if (_socket?.State != WebSocketState.Closed)
                _socket?.Abort();
            if (_sender is not null)
                await _sender.ConfigureAwait(false);
            if (_receiver is not null)
                await _receiver.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await StopAsync().ConfigureAwait(false);
        _socket?.Dispose();
        _lifetime.Dispose();
    }

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
            return;
        _lifetime.Cancel();
        Error?.Invoke(this, new(message, true));
    }

    private Task SendAsync<T>(T message, CancellationToken token) =>
        _socket!.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message)), WebSocketMessageType.Text, true, token);

    private async Task<JsonDocument> ReceiveAsync(CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var frame = await _socket!.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 1024 * 1024)
                throw new RealtimeProtocolException("Foundry cerró la conexión o envió una trama no admitida.");
            message.Write(buffer, 0, frame.Count);
            if (frame.EndOfMessage)
                return JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
        }
    }

    private static string? EventType(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var value) || value.ValueKind != JsonValueKind.String)
            throw new RealtimeProtocolException("Foundry devolvió un evento de traducción inválido.");
        var type = value.GetString();
        if (type == "error")
            throw new RealtimeProtocolException("Foundry rechazó la sesión o un evento. Revisa el despliegue, permisos y cuota.");
        return type;
    }

    internal static string SafeError(Exception exception, string prefix) => exception switch
    {
        RealtimeProtocolException error => $"{prefix}: {error.Message}",
        OperationCanceledException => $"{prefix}: tiempo de espera agotado o prueba cancelada.",
        _ => $"{prefix}: revisa la red y el endpoint de traducción."
    };
}

internal sealed class RealtimeProtocolException(string message) : InvalidOperationException(message);
