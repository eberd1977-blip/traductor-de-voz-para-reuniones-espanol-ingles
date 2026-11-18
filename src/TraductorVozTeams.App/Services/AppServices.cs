using TraductorVozTeams.App.Providers;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Secrets;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App.Services;

public sealed class AppServices
{
    public AppServices(AppPaths paths)
    {
        Paths = paths;
        SettingsStore = new SettingsStore(paths.SettingsFile);
        Settings = SettingsStore.Load();
        Registry = ProviderRegistry.CreateDefault();
        Profiles = new ProfileService(Settings, new DpapiSecretStore(paths.SecretsDirectory), Registry);
        Logger = new FileLogger(paths.LogsDirectory);
        Factories = new Dictionary<string, ITranslationProviderFactory>
        {
            [AzureSpeechDescriptor.Type] = new AzureSpeechProviderFactory(),
            [RealtimeTranslateDescriptor.Type] = new RealtimeTranslateFactory(),
        };
    }

    public AppPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public ProviderRegistry Registry { get; }

    public ProfileService Profiles { get; }

    public FileLogger Logger { get; }

    public IReadOnlyDictionary<string, ITranslationProviderFactory> Factories { get; }

    public void Save() => SettingsStore.Save(Settings);

    public ProviderSessionContext CreateContext(ProviderProfile profile, IReadOnlyDictionary<string, string>? valuesOverride = null,
        IReadOnlyDictionary<string, string>? secretsOverride = null)
    {
        var secrets = new Dictionary<string, string>(Profiles.LoadSecrets(profile));
        if (secretsOverride is not null)
        {
            foreach (var (key, value) in secretsOverride)
            {
                secrets[key] = value;
            }
        }

        return new ProviderSessionContext(valuesOverride ?? profile.Values, secrets, Settings.SourceLanguage, Settings.SegmentationSilenceMs, "en");
    }

    /// <summary>Mismo perfil, recurso y credenciales; solo cambian idiomas y voz de la dirección entrante.</summary>
    public ProviderSessionContext CreateIncomingContext(ProviderProfile profile)
    {
        var incoming = Settings.Incoming;
        if (profile.ProviderType == RealtimeTranslateDescriptor.Type)
            return CreateContext(profile) with { SourceLanguage = incoming.SourceLanguage, RealtimeTarget = "es" };
        return DirectionContext.WithLanguages(CreateContext(profile), incoming.SourceLanguage, incoming.TargetLanguage, incoming.VoiceName);
    }
}
