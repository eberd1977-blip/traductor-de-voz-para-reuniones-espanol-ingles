using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Secrets;

namespace TraductorVozTeams.Core.Settings;

/// <summary>
/// Separa valores no secretos (settings.json) de secretos (almacén cifrado)
/// según el descriptor de cada proveedor.
/// </summary>
public sealed class ProfileService(AppSettings settings, ISecretStore secrets, ProviderRegistry registry)
{
    public AppSettings Settings => settings;

    public static string SecretName(string profileId, string fieldKey) => $"{profileId}.{fieldKey}";

    public IProviderDescriptor DescriptorFor(ProviderProfile profile) =>
        registry.Find(profile.ProviderType)
        ?? throw new InvalidOperationException($"Proveedor desconocido: {profile.ProviderType}");

    public bool HasSecret(ProviderProfile profile, string fieldKey) => secrets.Exists(SecretName(profile.Id, fieldKey));

    /// <param name="fieldValues">Valores del formulario. Un secreto vacío conserva el actual.</param>
    public IReadOnlyList<string> Validate(ProviderProfile profile, IReadOnlyDictionary<string, string> fieldValues)
    {
        var descriptor = DescriptorFor(profile);
        var (plain, newSecrets) = Split(descriptor, fieldValues);
        var available = descriptor.Fields
            .Where(f => f.IsSecret && (newSecrets.ContainsKey(f.Key) || HasSecret(profile, f.Key)))
            .Select(f => f.Key)
            .ToHashSet();
        var errors = descriptor.Validate(plain, available).ToList();
        if (profile.ProviderType == RealtimeTranslateDescriptor.Type
            && RealtimeTranslateDescriptor.ResourceOrigin(profile.Values) != RealtimeTranslateDescriptor.ResourceOrigin(plain)
            && !newSecrets.ContainsKey(RealtimeTranslateDescriptor.ApiKey))
        {
            errors.Add("Si cambias el recurso Foundry, vuelve a ingresar su clave.");
        }
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Insert(0, "Indica un nombre para el perfil.");
        }

        return errors;
    }

    public void SaveProfile(ProviderProfile profile, IReadOnlyDictionary<string, string> fieldValues)
    {
        var descriptor = DescriptorFor(profile);
        var (plain, newSecrets) = Split(descriptor, fieldValues);
        if (profile.ProviderType == RealtimeTranslateDescriptor.Type)
        {
            var changedOrigin = RealtimeTranslateDescriptor.ResourceOrigin(profile.Values) != RealtimeTranslateDescriptor.ResourceOrigin(plain);
            if (changedOrigin && !newSecrets.ContainsKey(RealtimeTranslateDescriptor.ApiKey))
                throw new InvalidOperationException("Vuelve a ingresar la clave del nuevo recurso Foundry.");
            if (changedOrigin || GptRoute(profile.Values) != GptRoute(plain) || newSecrets.Count != 0)
                secrets.Delete(SecretName(profile.Id, "verifiedVoice"));
        }
        foreach (var (key, value) in newSecrets)
        {
            secrets.Set(SecretName(profile.Id, key), value);
        }
        profile.Values = new Dictionary<string, string>(plain);

        if (!settings.Profiles.Contains(profile))
        {
            settings.Profiles.Add(profile);
        }

        settings.ActiveProfileId ??= profile.Id;
        settings.Incoming.ProfileId ??= profile.Id;
    }

    public bool DeleteSecret(ProviderProfile profile, string fieldKey)
    {
        if (profile.ProviderType == RealtimeTranslateDescriptor.Type && fieldKey == RealtimeTranslateDescriptor.ApiKey)
            secrets.Delete(SecretName(profile.Id, "verifiedVoice"));
        return secrets.Delete(SecretName(profile.Id, fieldKey));
    }

    public bool CanStartMeeting(ProviderProfile profile) =>
        profile.ProviderType != RealtimeTranslateDescriptor.Type
        || (GptRoute(profile.Values) is { } route && HasSecret(profile, RealtimeTranslateDescriptor.ApiKey)
            && secrets.Get(SecretName(profile.Id, "verifiedVoice")) == route);

    public void ConfirmGptVoice(ProviderProfile profile)
    {
        if (profile.ProviderType != RealtimeTranslateDescriptor.Type || !settings.Profiles.Contains(profile)
            || GptRoute(profile.Values) is not { } route || !HasSecret(profile, RealtimeTranslateDescriptor.ApiKey))
            throw new InvalidOperationException("Guarda el perfil y su clave antes de verificarlo.");
        secrets.Set(SecretName(profile.Id, "verifiedVoice"), route);
    }

    private static string? GptRoute(IReadOnlyDictionary<string, string> values) =>
        RealtimeTranslateDescriptor.ResourceOrigin(values) is { } origin
            ? $"{origin}\n{ProviderValues.Get(values, RealtimeTranslateDescriptor.DeploymentKey)}"
            : null;

    public void DeleteProfile(ProviderProfile profile)
    {
        foreach (var field in DescriptorFor(profile).Fields.Where(f => f.IsSecret))
        {
            DeleteSecret(profile, field.Key);
        }

        settings.Profiles.Remove(profile);
        if (settings.ActiveProfileId == profile.Id)
        {
            settings.ActiveProfileId = null;
        }
        if (settings.Incoming.ProfileId == profile.Id)
        {
            settings.Incoming.ProfileId = null;
        }
    }

    public IReadOnlyDictionary<string, string> LoadSecrets(ProviderProfile profile)
    {
        var result = new Dictionary<string, string>();
        foreach (var field in DescriptorFor(profile).Fields.Where(f => f.IsSecret))
        {
            var value = secrets.Get(SecretName(profile.Id, field.Key));
            if (value is not null)
            {
                result[field.Key] = value;
            }
        }

        return result;
    }

    private static (Dictionary<string, string> Plain, Dictionary<string, string> Secrets) Split(
        IProviderDescriptor descriptor, IReadOnlyDictionary<string, string> fieldValues)
    {
        var plain = new Dictionary<string, string>();
        var secretValues = new Dictionary<string, string>();
        foreach (var field in descriptor.Fields)
        {
            if (!fieldValues.TryGetValue(field.Key, out var value))
            {
                continue;
            }

            if (field.IsSecret)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    secretValues[field.Key] = value.Trim();
                }
            }
            else
            {
                plain[field.Key] = value.Trim();
            }
        }

        return (plain, secretValues);
    }
}
