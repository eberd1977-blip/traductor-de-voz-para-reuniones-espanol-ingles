using System.Globalization;
using System.Text;

namespace TraductorVozTeams.Core.Metrics;

public sealed record StatsSummary(int Count, double Min, double Median, double P95, double Max)
{
    public override string ToString() => Count == 0
        ? "sin datos"
        : string.Create(CultureInfo.InvariantCulture,
            $"n={Count}; mín {Min:0} ms; mediana {Median:0} ms; p95 {P95:0} ms; máx {Max:0} ms");
}

public static class MeasurementStats
{
    public static StatsSummary Summarize(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return new StatsSummary(0, double.NaN, double.NaN, double.NaN, double.NaN);
        }

        return new StatsSummary(
            sorted.Count,
            sorted[0],
            LatencyTracker.Percentile(sorted, 0.5),
            LatencyTracker.Percentile(sorted, 0.95),
            sorted[^1]);
    }
}

/// <summary>
/// Tasa de error por palabras (WER) entre una referencia y una hipótesis. Ignora mayúsculas,
/// tildes y puntuación. Es solo orientativa para traducciones: hay más de una traducción válida.
/// </summary>
public static class WordErrorRate
{
    public static double Compute(string reference, string hypothesis)
    {
        var r = Tokenize(reference);
        var h = Tokenize(hypothesis);
        if (r.Count == 0)
        {
            return h.Count == 0 ? 0 : 1;
        }

        var previous = new int[h.Count + 1];
        var current = new int[h.Count + 1];
        for (var j = 0; j <= h.Count; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= r.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= h.Count; j++)
            {
                var cost = r[i - 1] == h[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return (double)previous[h.Count] / r.Count;
    }

    public static IReadOnlyList<string> Tokenize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) || c == '\'' ? char.ToLowerInvariant(c) : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
