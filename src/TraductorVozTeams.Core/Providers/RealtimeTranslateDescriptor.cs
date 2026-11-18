namespace TraductorVozTeams.Core.Providers;

public sealed class RealtimeTranslateDescriptor : IProviderDescriptor
{
    public const string Type = "foundry-realtime-translate";
    public const string EndpointKey = "endpoint";
    public const string DeploymentKey = "deployment";
    public const string ApiKey = "apiKey";

    public string ProviderType => Type;
    public string DisplayName => "GPT Realtime Translate";
    public bool IsAvailable => true;
    public string? AvailabilityNote => null;
    public IReadOnlyList<ProviderField> Fields { get; } =
    [
        new(EndpointKey, "Endpoint del recurso", ProviderFieldKind.Text, Help: "Origen HTTPS del recurso de Microsoft Foundry, sin rutas ni parámetros."),
        new(DeploymentKey, "Despliegue", ProviderFieldKind.Text, "gpt-realtime-translate"),
        new(ApiKey, "Clave del recurso", ProviderFieldKind.Secret, Help: "Déjala vacía para conservar la clave guardada.")
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values, IReadOnlySet<string> availableSecrets)
    {
        var errors = new List<string>();
        if (ResourceOrigin(values) is null)
            errors.Add("Indica el origen HTTPS de un recurso Azure (.services.ai.azure.com o .openai.azure.com).");
        var deployment = ProviderValues.Get(values, DeploymentKey);
        if (deployment.Length is 0 or > 128 || deployment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            errors.Add("Indica un nombre de despliegue válido.");
        if (!availableSecrets.Contains(ApiKey))
            errors.Add("Ingresa la clave del recurso Foundry.");
        return errors;
    }

    public static string? ResourceOrigin(IReadOnlyDictionary<string, string> values)
    {
        if (!Uri.TryCreate(ProviderValues.Get(values, EndpointKey), UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !uri.IsDefaultPort || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !(uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
