using TraductorVozTeams.App.Audio;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Metrics;
using TraductorVozTeams.Core.Providers;

namespace TraductorVozTeams.App.Services;

public sealed record IncomingDevices(string CaptureId, string ListenId, float TranslationVolume, float OriginalVolume, bool DuckOriginal, float DuckLevel);

/// <summary>
/// Dirección entrante: cable B (audio de la reunión) → proveedor (inglés → español) → auriculares.
/// Opcionalmente reproduce también el inglés original en los auriculares, a volumen configurable.
/// Nada de esta dirección se reproduce en un cable virtual, por lo que no vuelve a la reunión.
/// Es un circuito paralelo: no comparte objetos de audio con <see cref="TranslationSession"/>.
/// </summary>
public sealed class IncomingTranslationSession(ITranslationProvider provider, IncomingDevices devices, FileLogger logger) : IAsyncDisposable
{
    /// <summary>Si el inglés original acumula más retraso que esto (deriva de relojes), se descarta.</summary>
    private static readonly TimeSpan MaxOriginalLag = TimeSpan.FromMilliseconds(400);

    private readonly object _volumeGate = new();
    private MicrophoneCapture? _capture;
    private AudioOutput? _translationOutput;
    private AudioOutput? _originalOutput;
    private float _originalVolume = devices.OriginalVolume;
    private bool _duck = devices.DuckOriginal;
    private float _duckLevel = devices.DuckLevel;
    private bool _streamStarted;

    public LatencyTracker Latency { get; } = new();

    public UsageMeter Usage { get; } = new("entrante");

    public event EventHandler<TranslationTextEventArgs>? Recognizing;

    public event EventHandler<TranslationTextEventArgs>? Recognized;

    public event EventHandler<UtteranceLatency>? UtteranceCompleted;

    public event EventHandler<float>? LevelChanged;

    public event EventHandler<string>? Failed;

    // Igual que la dirección saliente: todo objeto COM de WASAPI se crea y usa en MTA (Task.Run), nunca en el STA de WPF.
    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        _translationOutput = new AudioOutput(AudioDeviceService.Open(devices.ListenId), provider.OutputFormat, devices.TranslationVolume);
        _originalOutput = new AudioOutput(AudioDeviceService.Open(devices.ListenId), provider.InputFormat, 0f);

        provider.Recognizing += OnRecognizing;
        provider.Recognized += OnRecognized;
        provider.AudioReceived += OnAudioReceived;
        provider.Error += OnProviderError;
        await provider.StartAsync(cancellationToken).ConfigureAwait(false);

        _capture = new MicrophoneCapture(AudioDeviceService.Open(devices.CaptureId), provider.InputFormat);
        _capture.DataReady += OnMeetingAudio;
        _capture.LevelChanged += level => LevelChanged?.Invoke(this, level);
        _capture.Stopped += ex =>
        {
            if (ex is not null)
            {
                Failed?.Invoke(this, "Se detuvo la captura del audio de la reunión: " + ex.Message);
            }
        };
        Usage.Start(provider.InputFormat.AverageBytesPerSecond);
        logger.Info($"[entrante] Captura: {_capture.SourceFormatDescription}; auriculares: {_translationOutput.DeviceName}; inglés original: {_originalVolume:P0}");
        _capture.Start();
    }, cancellationToken);

    public void SetVolumes(float translation, float original, bool duck, float duckLevel)
    {
        if (_translationOutput is not null)
        {
            _translationOutput.Volume = translation;
        }

        lock (_volumeGate)
        {
            _originalVolume = Math.Clamp(original, 0f, 1f);
            _duck = duck;
            _duckLevel = Math.Clamp(duckLevel, 0f, 1f);
        }

        if (original <= 0f)
        {
            _originalOutput?.Clear();
        }
    }

    public Task<UsageSnapshot> StopAsync() => Task.Run(async () =>
    {
        _capture?.Stop();
        try
        {
            await provider.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error("[entrante] Error al detener el proveedor", ex);
        }

        var usage = Usage.Stop();
        logger.Info($"[entrante] Audio enviado al proveedor: {usage.AudioSent.TotalSeconds:0.0} s; frases: {usage.Utterances}");
        return usage;
    });

    public ValueTask DisposeAsync() => new(Task.Run(async () =>
    {
        provider.Recognizing -= OnRecognizing;
        provider.Recognized -= OnRecognized;
        provider.AudioReceived -= OnAudioReceived;
        provider.Error -= OnProviderError;
        _capture?.Dispose();
        await provider.DisposeAsync().ConfigureAwait(false);
        _translationOutput?.Dispose();
        _originalOutput?.Dispose();
    }));

    /// <summary>Volumen efectivo del inglés original, atenuado mientras suena la traducción.</summary>
    public static float OriginalGain(float volume, bool duck, float duckLevel, bool translationPlaying) =>
        duck && translationPlaying ? volume * duckLevel : volume;

    private void OnMeetingAudio(byte[] buffer, int count)
    {
        if (!_streamStarted)
        {
            _streamStarted = true;
            Latency.MarkStreamStarted(DateTimeOffset.UtcNow);
        }

        provider.PushAudio(buffer, count);
        Usage.AddAudio(count);

        float volume;
        bool duck;
        float duckLevel;
        lock (_volumeGate)
        {
            (volume, duck, duckLevel) = (_originalVolume, _duck, _duckLevel);
        }

        var original = _originalOutput;
        if (original is null)
        {
            return;
        }

        if (volume <= 0f)
        {
            return;
        }

        var playing = _translationOutput is { } t && t.BufferedDuration > TimeSpan.Zero;
        original.Volume = OriginalGain(volume, duck, duckLevel, playing);
        if (original.BufferedDuration > MaxOriginalLag)
        {
            original.Clear();
        }

        original.Enqueue(new AudioChunk(buffer.AsSpan(0, count).ToArray(), provider.InputFormat));
    }

    private void OnRecognizing(object? sender, TranslationTextEventArgs e) => Recognizing?.Invoke(this, e);

    private void OnRecognized(object? sender, TranslationTextEventArgs e)
    {
        if (!e.IsContinuousDelta)
        {
            Latency.OnRecognized(DateTimeOffset.UtcNow, e.AudioOffset, e.AudioDuration, e.SourceText, e.TranslatedText);
            Usage.AddUtterance(e.SourceText, e.TranslatedText);
        }
        Recognized?.Invoke(this, e);
    }

    private void OnAudioReceived(object? sender, TranslatedAudioEventArgs e)
    {
        if (e.Chunk.IsEmpty)
        {
            Latency.OnAudio(DateTimeOffset.UtcNow, isEndOfUtterance: true);
            return;
        }

        _translationOutput?.Enqueue(e.Chunk);
        var completed = Latency.OnAudio(DateTimeOffset.UtcNow, isEndOfUtterance: false);
        if (completed is not null)
        {
            UtteranceCompleted?.Invoke(this, completed);
        }
    }

    private void OnProviderError(object? sender, ProviderErrorEventArgs e) => Failed?.Invoke(this, "[entrante] " + e.Message);
}
