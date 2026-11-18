using System.Text.RegularExpressions;

namespace TraductorVozTeams.Core.Providers;

public sealed partial class AzureSpeechDescriptor : IProviderDescriptor
{
    public const string Type = "azure-speech";
    public const string ConnectionModeKey = "connectionMode";
    public const string RegionKey = "region";
    public const string EndpointKey = "endpoint";
    public const string ApiKeyKey = "apiKey";
    public const string VoiceNameKey = "voiceName";
    public const string TargetLanguageKey = "targetLanguage";
    public const string CustomEndpointIdKey = "customEndpointId";
    public const string ModeRegion = "region";
    public const string ModeEndpoint = "endpoint";

    public string ProviderType => Type;

    public string DisplayName => "Azure AI Speech";

    public bool IsAvailable => true;

    public string? AvailabilityNote => null;

    public IReadOnlyList<ProviderField> Fields { get; } =
    [
        new(ConnectionModeKey, "Modo de conexión", ProviderFieldKind.Choice, ModeRegion, [ModeRegion, ModeEndpoint],
            "\"region\": clave + región. \"endpoint\": clave + endpoint del recurso."),
        new(RegionKey, "Región", ProviderFieldKind.Text, null, null,
            "Región del recurso tal como aparece en Azure (p. ej. eastus). Solo en modo region."),
        new(EndpointKey, "Endpoint", ProviderFieldKind.Text, null, null,
            "URL https:// o wss:// del recurso. Solo en modo endpoint."),
        new(ApiKeyKey, "Clave del recurso", ProviderFieldKind.Secret, null, null,
            "Se guarda cifrada para tu usuario de Windows. Déjala vacía para conservar la actual."),
        new(VoiceNameKey, "Voz en inglés (modelo de síntesis)", ProviderFieldKind.Text, "en-US-JennyNeural", null,
            "Nombre de voz neuronal de Azure AI Speech. También puedes elegirla desde la lista de la ventana principal."),
        new(TargetLanguageKey, "Idioma destino", ProviderFieldKind.Text, "en", null,
            "Código de idioma de traducción (v1: inglés, \"en\")."),
        new(CustomEndpointIdKey, "ID de modelo personalizado (opcional)", ProviderFieldKind.Text, null, null,
            "Solo si usas un modelo de reconocimiento personalizado (Custom Speech)."),
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values, IReadOnlySet<string> availableSecrets)
    {
        var errors = new List<string>();
        var mode = ProviderValues.Get(values, ConnectionModeKey);
        if (mode.Length == 0)
        {
            mode = ModeRegion;
        }

        if (mode == ModeRegion)
        {
            var region = ProviderValues.Get(values, RegionKey);
            if (region.Length == 0)
            {
                errors.Add("Indica la región del recurso de Azure AI Speech.");
            }
            else if (!RegionPattern().IsMatch(region))
            {
                errors.Add("La región solo admite letras minúsculas y números (p. ej. eastus).");
            }
        }
        else if (mode == ModeEndpoint)
        {
            var endpoint = ProviderValues.Get(values, EndpointKey);
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "wss"))
            {
                errors.Add("El endpoint debe ser una URL absoluta https:// o wss://.");
            }
        }
        else
        {
            errors.Add($"Modo de conexión desconocido: \"{mode}\".");
        }

        if (!availableSecrets.Contains(ApiKeyKey))
        {
            errors.Add("Introduce la clave del recurso de Azure AI Speech.");
        }

        if (ProviderValues.Get(values, VoiceNameKey).Length == 0)
        {
            errors.Add("Indica la voz en inglés.");
        }

        if (ProviderValues.Get(values, TargetLanguageKey).Length == 0)
        {
            errors.Add("Indica el idioma destino.");
        }

        return errors;
    }

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex RegionPattern();
}
