namespace TraductorVozTeams.Core.Providers;

public enum ProviderFieldKind
{
    Text,
    Choice,
    Secret,
}

/// <summary>Describe un campo configurable de un proveedor; la UI genera el formulario a partir de esto.</summary>
public sealed record ProviderField(
    string Key,
    string Label,
    ProviderFieldKind Kind,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null,
    string? Help = null)
{
    public bool IsSecret => Kind == ProviderFieldKind.Secret;
}

public interface IProviderDescriptor
{
    string ProviderType { get; }

    string DisplayName { get; }

    /// <summary>False para integraciones futuras: se muestran pero no se pueden usar.</summary>
    bool IsAvailable { get; }

    string? AvailabilityNote { get; }

    IReadOnlyList<ProviderField> Fields { get; }

    /// <param name="values">Valores no secretos.</param>
    /// <param name="availableSecrets">Claves de campos secretos que ya tienen valor (guardado o recién escrito).</param>
    IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values, IReadOnlySet<string> availableSecrets);
}

public static class ProviderValues
{
    public static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
}
