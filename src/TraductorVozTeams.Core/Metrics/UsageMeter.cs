using System.Globalization;
using System.Text;

namespace TraductorVozTeams.Core.Metrics;

public sealed record UsageSnapshot(
    string Direction,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    TimeSpan Active,
    TimeSpan AudioSent,
    int Utterances,
    int SourceCharacters,
    int TranslatedCharacters)
{
    public double AudioSentHours => AudioSent.TotalHours;
}

/// <summary>
/// Mide localmente el consumo de una dirección: audio enviado al servicio y texto traducido.
/// Es una estimación para comparar con la facturación real de Azure; no sustituye a Cost Management.
/// </summary>
public sealed class UsageMeter(string direction, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private DateTimeOffset? _started;
    private DateTimeOffset? _ended;
    private long _bytesSent;
    private int _bytesPerSecond;
    private int _utterances;
    private int _sourceChars;
    private int _translatedChars;

    public string Direction { get; } = direction;

    public void Start(int inputBytesPerSecond)
    {
        lock (_gate)
        {
            _started = _clock();
            _ended = null;
            _bytesSent = 0;
            _bytesPerSecond = inputBytesPerSecond;
            _utterances = 0;
            _sourceChars = 0;
            _translatedChars = 0;
        }
    }

    public void AddAudio(long bytes)
    {
        lock (_gate)
        {
            _bytesSent += bytes;
        }
    }

    public void AddUtterance(string sourceText, string translatedText)
    {
        lock (_gate)
        {
            _utterances++;
            _sourceChars += sourceText.Length;
            _translatedChars += translatedText.Length;
        }
    }

    public UsageSnapshot Stop()
    {
        lock (_gate)
        {
            _ended ??= _clock();
            return SnapshotLocked();
        }
    }

    public UsageSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    private UsageSnapshot SnapshotLocked()
    {
        var start = _started ?? _clock();
        var end = _ended ?? _clock();
        var audio = _bytesPerSecond > 0 ? TimeSpan.FromSeconds((double)_bytesSent / _bytesPerSecond) : TimeSpan.Zero;
        return new UsageSnapshot(Direction, start, _ended, end - start, audio, _utterances, _sourceChars, _translatedChars);
    }

    public static string CsvHeader => "direccion,inicio_utc,fin_utc,activo_s,audio_enviado_s,audio_enviado_h,frases,caracteres_origen,caracteres_traducidos";

    public static string ToCsvLine(UsageSnapshot s) => string.Join(',',
        s.Direction,
        s.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
        s.EndedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        s.Active.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
        s.AudioSent.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
        s.AudioSentHours.ToString("0.0000", CultureInfo.InvariantCulture),
        s.Utterances.ToString(CultureInfo.InvariantCulture),
        s.SourceCharacters.ToString(CultureInfo.InvariantCulture),
        s.TranslatedCharacters.ToString(CultureInfo.InvariantCulture));

    /// <summary>Agrega una línea a metrics\consumo-AAAAMMDD.csv (crea el encabezado si hace falta).</summary>
    public static string AppendToDailyCsv(string metricsDirectory, UsageSnapshot snapshot)
    {
        Directory.CreateDirectory(metricsDirectory);
        var file = Path.Combine(metricsDirectory, $"consumo-{snapshot.StartedUtc.ToLocalTime():yyyyMMdd}.csv");
        var builder = new StringBuilder();
        if (!File.Exists(file))
        {
            builder.AppendLine(CsvHeader);
        }

        builder.AppendLine(ToCsvLine(snapshot));
        File.AppendAllText(file, builder.ToString(), Encoding.UTF8);
        return file;
    }
}
