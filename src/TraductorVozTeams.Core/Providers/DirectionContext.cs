namespace TraductorVozTeams.Core.Providers;

/// <summary>
/// Deriva el contexto de una dirección a partir del mismo perfil (mismo recurso y credenciales),
/// cambiando solo idioma de origen, idioma destino y voz. El perfil guardado no se modifica.
/// </summary>
public static class DirectionContext
{
    public static ProviderSessionContext WithLanguages(
        ProviderSessionContext baseContext,
        string sourceLanguage,
        string targetLanguage,
        string? voiceName)
    {
        var values = new Dictionary<string, string>(baseContext.Values)
        {
            [AzureSpeechDescriptor.TargetLanguageKey] = targetLanguage,
        };

        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            values[AzureSpeechDescriptor.VoiceNameKey] = voiceName;
        }

        return baseContext with { Values = values, SourceLanguage = sourceLanguage };
    }
}
