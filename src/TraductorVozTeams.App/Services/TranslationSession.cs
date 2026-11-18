using TraductorVozTeams.App.Audio;
using TraductorVozTeams.Core.Metrics;
using TraductorVozTeams.Core.Providers;

namespace TraductorVozTeams.App.Services;

public sealed record SessionDevices(string InputId, string TeamsOutputId, string? MonitorId, float TeamsVolume, float MonitorVolume);

/// <summary>
/// Orquesta micrófono → proveedor → salidas. El audio original solo se envía al proveedor;
/// la salida a Teams y el monitor reciben únicamente el audio traducido.
/// </summary>
public sealed class TranslationSession(ITranslationProvider provider, SessionDevices devices, FileLogger logger) : IAsyncDisposable
{
    private MicrophoneCapture? _capture;
    private AudioOutput? _teamsOutput;
    private AudioOutput? _monitorOutput;
    private bool _streamStarted;
    private long _bytesSent;

    public LatencyTracker Latency { get; } = new();

    /// <summary>Audio enviado al proveedor en esta sesión. La traducción de voz de Azure se factura por segundo de audio enviado.</summary>
    public TimeSpan AudioSent => TimeSpan.FromSeconds((double)Interlocked.Read(ref _bytesSent) / provider.InputFormat.AverageBytesPerSecond);
    public long AudioBytes => Interlocked.Read(ref _bytesSent);

    public event EventHandler<TranslationTextEventArgs>? Recognizing;

    public event EventHandler<TranslationTextEventArgs>? Recognized;

    public event EventHandler<UtteranceLatency>? UtteranceCompleted;

    public event EventHandler<float>? LevelChanged;

    public event EventHandler<string>? Failed;

    // Los dispositivos COM de WASAPI no deben crearse en el STA de WPF y usarse luego desde MTA.
    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        _teamsOutput = new AudioOutput(AudioDeviceService.Open(devices.TeamsOutputId), provider.OutputFormat, devices.TeamsVolume);
        if (devices.MonitorId is not null)
        {
            _monitorOutput = new AudioOutput(AudioDeviceService.Open(devices.MonitorId), provider.OutputFormat, devices.MonitorVolume);
        }

        provider.Recognizing += OnRecognizing;
        provider.Recognized += OnRecognized;
        provider.AudioReceived += OnAudioReceived;
        provider.Error += OnProviderError;
        await provider.StartAsync(cancellationToken).ConfigureAwait(false);

        _capture = new MicrophoneCapture(AudioDeviceService.Open(devices.InputId), provider.InputFormat);
        _capture.DataReady += OnMicrophoneData;
        _capture.LevelChanged += level => LevelChanged?.Invoke(this, level);
        _capture.Stopped += ex =>
        {
            if (ex is not null)
            {
                Failed?.Invoke(this, "Se detuvo la captura del micrófono: " + ex.Message);
            }
        };
        logger.Info($"Captura: {_capture.SourceFormatDescription}; salida Teams: {_teamsOutput.DeviceName}; monitor: {_monitorOutput?.DeviceName ?? "no"}");
        _capture.Start();
    }, cancellationToken);

    public void SetVolumes(float teams, float monitor)
    {
        if (_teamsOutput is not null)
        {
            _teamsOutput.Volume = teams;
        }

        if (_monitorOutput is not null)
        {
            _monitorOutput.Volume = monitor;
        }
    }

    public Task StopAsync() => Task.Run(async () =>
    {
        _capture?.Stop();
        try
        {
            await provider.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error("Error al detener el proveedor", ex);
        }

        logger.Info($"Audio enviado al proveedor en esta sesión: {AudioSent.TotalSeconds:0.0} s");
    });

    public ValueTask DisposeAsync() => new(Task.Run(async () =>
    {
        provider.Recognizing -= OnRecognizing;
        provider.Recognized -= OnRecognized;
        provider.AudioReceived -= OnAudioReceived;
        provider.Error -= OnProviderError;
        _capture?.Dispose();
        await provider.DisposeAsync().ConfigureAwait(false);
        _teamsOutput?.Dispose();
        _monitorOutput?.Dispose();
    }));

    private void OnMicrophoneData(byte[] buffer, int count)
    {
        if (!_streamStarted)
        {
            _streamStarted = true;
            Latency.MarkStreamStarted(DateTimeOffset.UtcNow);
        }

        provider.PushAudio(buffer, count);
        Interlocked.Add(ref _bytesSent, count);
    }

    private void OnRecognizing(object? sender, TranslationTextEventArgs e) => Recognizing?.Invoke(this, e);

    private void OnRecognized(object? sender, TranslationTextEventArgs e)
    {
        if (!e.IsContinuousDelta)
            Latency.OnRecognized(DateTimeOffset.UtcNow, e.AudioOffset, e.AudioDuration, e.SourceText, e.TranslatedText);
        Recognized?.Invoke(this, e);
    }

    private void OnAudioReceived(object? sender, TranslatedAudioEventArgs e)
    {
        if (e.Chunk.IsEmpty)
        {
            Latency.OnAudio(DateTimeOffset.UtcNow, isEndOfUtterance: true);
            return;
        }

        _teamsOutput?.Enqueue(e.Chunk);
        _monitorOutput?.Enqueue(e.Chunk);
        var completed = Latency.OnAudio(DateTimeOffset.UtcNow, isEndOfUtterance: false);
        if (completed is not null)
        {
            UtteranceCompleted?.Invoke(this, completed);
        }
    }

    private void OnProviderError(object? sender, ProviderErrorEventArgs e) => Failed?.Invoke(this, e.Message);
}
