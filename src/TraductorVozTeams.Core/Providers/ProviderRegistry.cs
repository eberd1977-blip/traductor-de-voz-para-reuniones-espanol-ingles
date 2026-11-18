namespace TraductorVozTeams.Core.Providers;

/// <summary>
/// Proveedor previsto para una etapa posterior. Se muestra en la UI pero no puede usarse:
/// cambiar solo el endpoint no lo hace compatible; requiere su propio adaptador
/// (protocolo, autenticación, modelos/despliegues y formatos de audio distintos).
/// </summary>
public sealed class FutureProviderDescriptor(string providerType, string displayName) : IProviderDescriptor
{
    public string ProviderType { get; } = providerType;

    public string DisplayName { get; } = displayName + " (futuro)";

    public bool IsAvailable => false;

    public string? AvailabilityNote =>
        "Integración futura. Usa otro protocolo, autenticación y formato de audio que Azure AI Speech; "
        + "cambiar solo el endpoint no garantiza compatibilidad. Requiere un adaptador propio.";

    public IReadOnlyList<ProviderField> Fields { get; } =
    [
        new("endpoint", "Endpoint", ProviderFieldKind.Text),
        new("deployment", "Modelo o despliegue", ProviderFieldKind.Text),
        new("apiVersion", "Versión de API", ProviderFieldKind.Text),
        new("apiKey", "Clave", ProviderFieldKind.Secret),
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values, IReadOnlySet<string> availableSecrets) =>
        [$"{DisplayName}: todavía no está disponible en esta versión."];
}

public sealed class ProviderRegistry
{
    private readonly List<IProviderDescriptor> _descriptors;

    public ProviderRegistry(IEnumerable<IProviderDescriptor> descriptors)
    {
        _descriptors = descriptors.ToList();
    }

    public static ProviderRegistry CreateDefault() => new(
    [
        new AzureSpeechDescriptor(),
        new RealtimeTranslateDescriptor(),
        new FutureProviderDescriptor("azure-openai", "Azure OpenAI"),
        new FutureProviderDescriptor("openai", "OpenAI"),
    ]);

    public IReadOnlyList<IProviderDescriptor> All => _descriptors;

    public IProviderDescriptor? Find(string providerType) =>
        _descriptors.FirstOrDefault(d => d.ProviderType == providerType);
}
