namespace TraductorVozTeams.Core.Providers;

public sealed record TranslationVoice(string Name, string DisplayName, string Locale, string Gender, bool IsMultilingual)
{
    public override string ToString() =>
        $"{DisplayName} · {Locale} · {Gender}{(IsMultilingual ? " · multilingüe" : string.Empty)}";
}

/// <summary>Capacidad opcional de un proveedor: listar y escuchar las voces de síntesis.</summary>
public interface IVoiceCatalog
{
    Task<IReadOnlyList<TranslationVoice>> ListVoicesAsync(ProviderSessionContext context, CancellationToken cancellationToken);

    /// <param name="outputDeviceId">Identificador WASAPI del dispositivo donde se escuchará la muestra.</param>
    Task<ConnectionTestResult> PreviewVoiceAsync(ProviderSessionContext context, string voiceName, string outputDeviceId,
        CancellationToken cancellationToken);
}

public static class VoiceFilter
{
    /// <summary>
    /// Voces del idioma destino (p. ej. "en" → en-US, en-GB…), sin duplicados y ordenadas
    /// con la variante principal primero. Excluye voces HD (nombre con ":"), cuyo uso en
    /// traducción de voz no está documentado.
    /// </summary>
    public static IReadOnlyList<TranslationVoice> ForTargetLanguage(IEnumerable<TranslationVoice> voices, string targetLanguage)
    {
        var language = targetLanguage.Trim();
        var prefix = language.Split('-')[0];
        return voices
            .Where(v => !v.Name.Contains(':'))
            .Where(v => v.Locale.Equals(language, StringComparison.OrdinalIgnoreCase)
                        || v.Locale.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v.Locale.Equals(PrimaryLocale(prefix), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(v => v.Locale, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string PrimaryLocale(string prefix) => prefix.ToLowerInvariant() switch
    {
        "en" => "en-US",
        "es" => "es-ES",
        "pt" => "pt-BR",
        "fr" => "fr-FR",
        "de" => "de-DE",
        _ => prefix,
    };
}
