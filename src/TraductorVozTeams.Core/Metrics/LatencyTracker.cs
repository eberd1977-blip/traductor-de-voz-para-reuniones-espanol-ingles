using System.Globalization;

namespace TraductorVozTeams.Core.Metrics;

public sealed record UtteranceLatency(
    int Index,
    DateTimeOffset? SpeechEndUtc,
    DateTimeOffset RecognizedUtc,
    DateTimeOffset? FirstAudioUtc,
    string SourceText,
    string TranslatedText)
{
    /// <summary>Fin del habla → primer audio traducido enviado a la salida.</summary>
    public TimeSpan? EndToEnd => SpeechEndUtc is { } end && FirstAudioUtc is { } audio ? audio - end : null;

    public TimeSpan? SpeechEndToRecognized => SpeechEndUtc is { } end ? RecognizedUtc - end : null;

    public TimeSpan? RecognizedToAudio => FirstAudioUtc is { } audio ? audio - RecognizedUtc : null;
}

public sealed record LatencySummary(int Count, TimeSpan? Median, TimeSpan? P95, TimeSpan? Last);

/// <summary>
/// Mide la demora por frase con marcas de tiempo locales. Es una aproximación: el fin del habla
/// se deduce del offset/duración que devuelve el servicio respecto del inicio del flujo enviado.
/// </summary>
public sealed class LatencyTracker
{
    private readonly object _gate = new();
    private readonly List<UtteranceLatency> _completed = [];
    private readonly Queue<UtteranceLatency> _pending = new();
    private DateTimeOffset? _streamStartUtc;
    private bool _audioInProgress;
    private int _nextIndex = 1;

    public void MarkStreamStarted(DateTimeOffset utc)
    {
        lock (_gate)
        {
            _streamStartUtc = utc;
        }
    }

    public void OnRecognized(DateTimeOffset nowUtc, TimeSpan? offset, TimeSpan? duration, string sourceText, string translatedText)
    {
        lock (_gate)
        {
            DateTimeOffset? speechEnd = _streamStartUtc is { } start && offset is { } o && duration is { } d ? start + o + d : null;
            _pending.Enqueue(new UtteranceLatency(_nextIndex++, speechEnd, nowUtc, null, sourceText, translatedText));
        }
    }

    /// <param name="isEndOfUtterance">True cuando el proveedor indica el fin del audio de una frase.</param>
    /// <returns>La frase completada cuando este fragmento es su primer audio.</returns>
    public UtteranceLatency? OnAudio(DateTimeOffset nowUtc, bool isEndOfUtterance)
    {
        lock (_gate)
        {
            if (isEndOfUtterance)
            {
                _audioInProgress = false;
                return null;
            }

            if (_audioInProgress || _pending.Count == 0)
            {
                return null;
            }

            _audioInProgress = true;
            var completed = _pending.Dequeue() with { FirstAudioUtc = nowUtc };
            _completed.Add(completed);
            return completed;
        }
    }

    public IReadOnlyList<UtteranceLatency> Completed
    {
        get
        {
            lock (_gate)
            {
                return [.. _completed];
            }
        }
    }

    public LatencySummary GetSummary()
    {
        var values = Completed.Select(u => u.EndToEnd).OfType<TimeSpan>().Select(t => t.TotalMilliseconds).ToList();
        var last = Completed.LastOrDefault()?.EndToEnd;
        if (values.Count == 0)
        {
            return new LatencySummary(0, null, null, last);
        }

        values.Sort();
        return new LatencySummary(
            values.Count,
            TimeSpan.FromMilliseconds(Percentile(values, 0.5)),
            TimeSpan.FromMilliseconds(Percentile(values, 0.95)),
            last);
    }

    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            throw new ArgumentException("Lista vacía.", nameof(sorted));
        }

        var rank = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
    }

    public static string CsvHeader => "indice,fin_habla_utc,reconocido_utc,primer_audio_utc,fin_a_reconocido_ms,reconocido_a_audio_ms,total_ms,texto_es,texto_en";

    public static string ToCsvLine(UtteranceLatency u) => string.Join(',',
        u.Index.ToString(CultureInfo.InvariantCulture),
        u.SpeechEndUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        u.RecognizedUtc.ToString("O", CultureInfo.InvariantCulture),
        u.FirstAudioUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        Ms(u.SpeechEndToRecognized),
        Ms(u.RecognizedToAudio),
        Ms(u.EndToEnd),
        Quote(u.SourceText),
        Quote(u.TranslatedText));

    private static string Ms(TimeSpan? value) =>
        value?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
