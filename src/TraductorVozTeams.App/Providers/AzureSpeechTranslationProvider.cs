using System.Globalization;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Providers;

namespace TraductorVozTeams.App.Providers;

/// <summary>Adaptador v1: Azure AI Speech, traducción de voz estándar con salida de voz sintetizada.</summary>
public sealed class AzureSpeechTranslationProvider(ProviderSessionContext context) : ITranslationProvider
{
    private readonly string _targetLanguage = TargetLanguage(context);
    private PushAudioInputStream? _stream;
    private AudioConfig? _audioConfig;
    private TranslationRecognizer? _recognizer;

    public event EventHandler<TranslationTextEventArgs>? Recognizing;

    public event EventHandler<TranslationTextEventArgs>? Recognized;

    public event EventHandler<TranslatedAudioEventArgs>? AudioReceived;

    public event EventHandler<ProviderErrorEventArgs>? Error;

    public PcmFormat InputFormat => PcmFormat.SpeechInput;

    public static SpeechTranslationConfig CreateConfig(ProviderSessionContext context)
    {
        var values = context.Values;
        if (!context.Secrets.TryGetValue(AzureSpeechDescriptor.ApiKeyKey, out var key) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("No hay clave guardada para este perfil.");
        }

        var mode = ProviderValues.Get(values, AzureSpeechDescriptor.ConnectionModeKey);
        var config = mode == AzureSpeechDescriptor.ModeEndpoint
            ? SpeechTranslationConfig.FromEndpoint(new Uri(ProviderValues.Get(values, AzureSpeechDescriptor.EndpointKey)), key)
            : SpeechTranslationConfig.FromSubscription(key, ProviderValues.Get(values, AzureSpeechDescriptor.RegionKey));

        config.SpeechRecognitionLanguage = context.SourceLanguage;
        config.AddTargetLanguage(TargetLanguage(context));
        config.VoiceName = ProviderValues.Get(values, AzureSpeechDescriptor.VoiceNameKey);

        var customEndpointId = ProviderValues.Get(values, AzureSpeechDescriptor.CustomEndpointIdKey);
        if (customEndpointId.Length > 0)
        {
            config.EndpointId = customEndpointId;
        }

        if (context.SegmentationSilenceMs > 0)
        {
            config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs,
                context.SegmentationSilenceMs.ToString(CultureInfo.InvariantCulture));
        }

        return config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = CreateConfig(context);
        _stream = AudioInputStream.CreatePushStream(
            AudioStreamFormat.GetWaveFormatPCM((uint)InputFormat.SampleRate, (byte)InputFormat.BitsPerSample, (byte)InputFormat.Channels));
        _audioConfig = AudioConfig.FromStreamInput(_stream);
        _recognizer = new TranslationRecognizer(config, _audioConfig);
        _recognizer.Recognizing += OnRecognizing;
        _recognizer.Recognized += OnRecognized;
        _recognizer.Synthesizing += OnSynthesizing;
        _recognizer.Canceled += OnCanceled;
        await _recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void PushAudio(byte[] buffer, int count) => _stream?.Write(buffer, count);

    public async Task StopAsync()
    {
        _stream?.Close();
        if (_recognizer is not null)
        {
            await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_recognizer is not null)
        {
            _recognizer.Recognizing -= OnRecognizing;
            _recognizer.Recognized -= OnRecognized;
            _recognizer.Synthesizing -= OnSynthesizing;
            _recognizer.Canceled -= OnCanceled;
            _recognizer.Dispose();
        }

        _audioConfig?.Dispose();
        _stream?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string TargetLanguage(ProviderSessionContext context)
    {
        var target = ProviderValues.Get(context.Values, AzureSpeechDescriptor.TargetLanguageKey);
        return target.Length == 0 ? "en" : target;
    }

    private string Translation(TranslationRecognitionResult result) =>
        result.Translations.TryGetValue(_targetLanguage, out var text) ? text : result.Translations.Values.FirstOrDefault() ?? string.Empty;

    private void OnRecognizing(object? sender, TranslationRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.TranslatingSpeech)
        {
            Recognizing?.Invoke(this, new TranslationTextEventArgs(e.Result.Text, Translation(e.Result), null, null));
        }
    }

    private void OnRecognized(object? sender, TranslationRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.TranslatedSpeech && e.Result.Text.Length > 0)
        {
            Recognized?.Invoke(this, new TranslationTextEventArgs(
                e.Result.Text, Translation(e.Result), TimeSpan.FromTicks((long)e.Result.OffsetInTicks), e.Result.Duration));
        }
    }

    private void OnSynthesizing(object? sender, TranslationSynthesisEventArgs e)
    {
        var audio = e.Result.GetAudio();
        if (audio.Length > 0)
        {
            var chunk = WavParser.Parse(audio, PcmFormat.SpeechInput);
            if (!chunk.IsEmpty)
            {
                AudioReceived?.Invoke(this, new TranslatedAudioEventArgs(chunk));
            }
        }

        // En sesiones continuas el SDK puede entregar un WAV completo por frase y
        // reservar SynthesizingAudioCompleted para el cierre de toda la sesion.
        if (WavParser.IsCompleteWave(audio) || audio.Length == 0 || e.Result.Reason == ResultReason.SynthesizingAudioCompleted)
        {
            AudioReceived?.Invoke(this, new TranslatedAudioEventArgs(new AudioChunk([], PcmFormat.SpeechInput)));
        }
    }

    private void OnCanceled(object? sender, TranslationRecognitionCanceledEventArgs e)
    {
        if (e.Reason == CancellationReason.Error)
        {
            Error?.Invoke(this, new ProviderErrorEventArgs($"Azure AI Speech: {e.ErrorCode} - {e.ErrorDetails}", true));
        }
    }
}

public sealed class AzureSpeechProviderFactory : ITranslationProviderFactory, IVoiceCatalog
{
    public const string PreviewText = "Hello, this is how my translated voice will sound in your Teams meetings.";
    public const string PreviewTextSpanish = "Hola, así sonará en tus auriculares la traducción de lo que digan en la reunión.";

    public static string PreviewTextFor(string targetLanguage) =>
        targetLanguage.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? PreviewTextSpanish : PreviewText;

    public string ProviderType => AzureSpeechDescriptor.Type;

    private static SpeechConfig CreateSpeechConfig(ProviderSessionContext context)
    {
        if (!context.Secrets.TryGetValue(AzureSpeechDescriptor.ApiKeyKey, out var key) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("No hay clave guardada para este perfil.");
        }

        var values = context.Values;
        return ProviderValues.Get(values, AzureSpeechDescriptor.ConnectionModeKey) == AzureSpeechDescriptor.ModeEndpoint
            ? SpeechConfig.FromEndpoint(new Uri(ProviderValues.Get(values, AzureSpeechDescriptor.EndpointKey)), key)
            : SpeechConfig.FromSubscription(key, ProviderValues.Get(values, AzureSpeechDescriptor.RegionKey));
    }

    /// <summary>Lista de voces de la región (consulta sin costo de síntesis), filtrada al idioma destino.</summary>
    public async Task<IReadOnlyList<TranslationVoice>> ListVoicesAsync(ProviderSessionContext context, CancellationToken cancellationToken)
    {
        var config = CreateSpeechConfig(context);
        using var synthesizer = new SpeechSynthesizer(config, null as AudioConfig);
        using var result = await synthesizer.GetVoicesAsync(string.Empty).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        if (result.Reason != ResultReason.VoicesListRetrieved)
        {
            throw new InvalidOperationException("Azure AI Speech no devolvió la lista de voces: " + result.ErrorDetails);
        }

        var target = ProviderValues.Get(context.Values, AzureSpeechDescriptor.TargetLanguageKey);
        var voices = result.Voices.Select(v => new TranslationVoice(
            v.ShortName,
            string.IsNullOrWhiteSpace(v.LocalName) ? v.ShortName : v.LocalName,
            v.Locale,
            v.Gender switch { SynthesisVoiceGender.Female => "mujer", SynthesisVoiceGender.Male => "hombre", _ => "neutra" },
            v.ShortName.Contains("Multilingual", StringComparison.OrdinalIgnoreCase)));
        return VoiceFilter.ForTargetLanguage(voices, target.Length == 0 ? "en" : target);
    }

    /// <summary>Sintetiza una frase corta (se factura como síntesis) y la reproduce en el dispositivo indicado.</summary>
    public async Task<ConnectionTestResult> PreviewVoiceAsync(ProviderSessionContext context, string voiceName, string outputDeviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = CreateSpeechConfig(context);
            config.SpeechSynthesisVoiceName = voiceName;
            using var audio = AudioConfig.FromSpeakerOutput(outputDeviceId);
            using var synthesizer = new SpeechSynthesizer(config, audio);
            using var result = await synthesizer.SpeakTextAsync(PreviewTextFor(ProviderValues.Get(context.Values, AzureSpeechDescriptor.TargetLanguageKey)))
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            if (result.Reason == ResultReason.Canceled)
            {
                var details = SpeechSynthesisCancellationDetails.FromResult(result);
                return new ConnectionTestResult(false, $"Error: {details.ErrorCode} - {details.ErrorDetails}");
            }

            return new ConnectionTestResult(true, $"Muestra reproducida con {voiceName}.");
        }
        catch (TimeoutException)
        {
            return new ConnectionTestResult(false, "Tiempo de espera agotado al sintetizar la muestra.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionTestResult(false, "Error: " + ex.Message);
        }
    }

    public ITranslationProvider Create(ProviderSessionContext context) => new AzureSpeechTranslationProvider(context);

    /// <summary>Envía ~1 s de silencio: valida clave, región/endpoint e idioma con un consumo mínimo.</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(ProviderSessionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var config = AzureSpeechTranslationProvider.CreateConfig(context);
            var format = PcmFormat.SpeechInput;
            using var stream = AudioInputStream.CreatePushStream(
                AudioStreamFormat.GetWaveFormatPCM((uint)format.SampleRate, (byte)format.BitsPerSample, (byte)format.Channels));
            using var audio = AudioConfig.FromStreamInput(stream);
            using var recognizer = new TranslationRecognizer(config, audio);
            stream.Write(new byte[format.AverageBytesPerSecond]);
            stream.Close();

            var result = await recognizer.RecognizeOnceAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (result.Reason == ResultReason.Canceled)
            {
                var details = CancellationDetails.FromResult(result);
                if (details.Reason == CancellationReason.Error)
                {
                    return new ConnectionTestResult(false, $"Error: {details.ErrorCode} - {details.ErrorDetails}");
                }
            }

            return new ConnectionTestResult(true, "Conexión correcta con Azure AI Speech.");
        }
        catch (TimeoutException)
        {
            return new ConnectionTestResult(false, "Tiempo de espera agotado al conectar con Azure AI Speech.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionTestResult(false, "Error: " + ex.Message);
        }
    }
}
