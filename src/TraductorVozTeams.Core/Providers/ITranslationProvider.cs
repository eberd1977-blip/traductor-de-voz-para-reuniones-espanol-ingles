using TraductorVozTeams.Core.Audio;

namespace TraductorVozTeams.Core.Providers;

public sealed record ProviderSessionContext(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> Secrets,
    string SourceLanguage,
    int SegmentationSilenceMs,
    string? RealtimeTarget = null);

public sealed class TranslationTextEventArgs(string sourceText, string translatedText, TimeSpan? audioOffset, TimeSpan? audioDuration) : EventArgs
{
    public string SourceText { get; } = sourceText;

    public string TranslatedText { get; } = translatedText;

    /// <summary>Inicio de la frase dentro del flujo de audio enviado.</summary>
    public TimeSpan? AudioOffset { get; } = audioOffset;

    public TimeSpan? AudioDuration { get; } = audioDuration;

    public bool IsContinuousDelta { get; init; }
}

public sealed class TranslatedAudioEventArgs(AudioChunk chunk) : EventArgs
{
    /// <summary>Fragmento de audio traducido. Vacío indica fin del audio de una frase.</summary>
    public AudioChunk Chunk { get; } = chunk;
}

public sealed class ProviderErrorEventArgs(string message, bool isFatal) : EventArgs
{
    public string Message { get; } = message;

    public bool IsFatal { get; } = isFatal;
}

public sealed record ConnectionTestResult(bool Success, string Message);

/// <summary>Contrato común de todos los proveedores de traducción voz a voz.</summary>
public interface ITranslationProvider : IAsyncDisposable
{
    PcmFormat InputFormat { get; }
    PcmFormat OutputFormat => InputFormat;

    event EventHandler<TranslationTextEventArgs>? Recognizing;

    event EventHandler<TranslationTextEventArgs>? Recognized;

    event EventHandler<TranslatedAudioEventArgs>? AudioReceived;

    event EventHandler<ProviderErrorEventArgs>? Error;

    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Envía audio PCM en <see cref="InputFormat"/>.</summary>
    void PushAudio(byte[] buffer, int count);

    Task StopAsync();
}

public interface ITranslationProviderFactory
{
    string ProviderType { get; }

    ITranslationProvider Create(ProviderSessionContext context);

    Task<ConnectionTestResult> TestConnectionAsync(ProviderSessionContext context, CancellationToken cancellationToken);
}
