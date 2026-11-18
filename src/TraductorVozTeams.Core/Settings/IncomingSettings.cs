namespace TraductorVozTeams.Core.Settings;

/// <summary>
/// Configuración de la dirección entrante: la reunión reproduce en un cable virtual (cable B),
/// la app captura su otro extremo, traduce y reproduce el español en los auriculares.
/// </summary>
public sealed class IncomingSettings
{
    public const float DefaultOriginalVolume = 0.2f;

    /// <summary>Extremo de reproducción del cable B que se elige como altavoz en la plataforma de reunión.</summary>
    public string? MeetingOutputDeviceId { get; set; }

    /// <summary>Extremo de grabación del cable B que captura la app.</summary>
    public string? CaptureDeviceId { get; set; }

    /// <summary>Auriculares donde se escucha el español (y opcionalmente el inglés original).</summary>
    public string? ListenDeviceId { get; set; }

    /// <summary>Perfil de proveedor de esta dirección. Si falta, se usa el de la dirección saliente.</summary>
    public string? ProfileId { get; set; }

    public string SourceLanguage { get; set; } = "en-US";

    public string TargetLanguage { get; set; } = "es";

    public string VoiceName { get; set; } = "es-AR-ElenaNeural";

    public float TranslationVolume { get; set; } = 1.0f;

    /// <summary>Volumen del inglés original (0 = no se reproduce).</summary>
    public float OriginalVolume { get; set; } = DefaultOriginalVolume;

    public bool OriginalMuted { get; set; }

    /// <summary>Atenúa el inglés original mientras suena la traducción.</summary>
    public bool DuckOriginal { get; set; } = true;

    /// <summary>Fracción del volumen del inglés que se conserva durante la atenuación.</summary>
    public float DuckLevel { get; set; } = 0.3f;

    public CablePairing? VerifiedPairing { get; set; }

    public float EffectiveOriginalVolume => OriginalMuted ? 0f : Math.Clamp(OriginalVolume, 0f, 1f);
}

/// <summary>Resultado de la prueba de sonido: identifica los extremos por ID de Windows, no por nombre.</summary>
public sealed record CablePairing(string MeetingOutputDeviceId, string CaptureDeviceId, string? OutgoingOutputDeviceId, DateTimeOffset VerifiedAt)
{
    public bool Matches(string? meetingOutputId, string? captureId, string? outgoingOutputId) =>
        meetingOutputId == MeetingOutputDeviceId
        && captureId == CaptureDeviceId
        && outgoingOutputId == OutgoingOutputDeviceId;
}
