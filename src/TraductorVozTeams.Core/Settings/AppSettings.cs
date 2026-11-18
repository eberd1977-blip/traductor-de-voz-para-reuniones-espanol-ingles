using TraductorVozTeams.Core.Devices;

namespace TraductorVozTeams.Core.Settings;

/// <summary>Configuración no secreta del usuario. Nunca contiene claves.</summary>
public sealed class AppSettings
{
    /// <summary>v2 añade <see cref="Incoming"/> sin mover ni renombrar los campos v1 de la dirección saliente.</summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Dirección entrante (reunión en inglés → español en auriculares).</summary>
    public IncomingSettings Incoming { get; set; } = new();

    public List<ProviderProfile> Profiles { get; set; } = [];

    public string? ActiveProfileId { get; set; }

    public string SourceLanguage { get; set; } = "es-AR";

    public string? InputDeviceId { get; set; }

    public string? TeamsOutputDeviceId { get; set; }

    public bool MonitorEnabled { get; set; }

    public string? MonitorDeviceId { get; set; }

    public float MonitorVolume { get; set; } = 0.8f;

    public float TeamsOutputVolume { get; set; } = 1.0f;

    /// <summary>0 = valor predeterminado del servicio.</summary>
    public int SegmentationSilenceMs { get; set; }

    public List<string> VirtualCablePatterns { get; set; } = [.. DeviceRoutingValidator.DefaultVirtualCablePatterns];

    /// <summary>Perfil de la dirección saliente (mi voz → reunión).</summary>
    public ProviderProfile? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);

    /// <summary>Perfil de la dirección entrante (reunión → auriculares).</summary>
    public ProviderProfile? IncomingProfile => Profiles.FirstOrDefault(p => p.Id == Incoming.ProfileId);
}

public sealed class ProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Azure AI Speech";

    public string ProviderType { get; set; } = string.Empty;

    /// <summary>Solo valores no secretos (endpoint, región, voz...).</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    public override string ToString() => Name;
}
