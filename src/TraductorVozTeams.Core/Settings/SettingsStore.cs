using System.Text.Json;

namespace TraductorVozTeams.Core.Settings;

public sealed class SettingsStore(string settingsFile)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Load()
    {
        if (!File.Exists(settingsFile))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(settingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
            settings.Incoming ??= new IncomingSettings();
            // Configuraciones previas compartían un perfil entre ambas direcciones.
            settings.Incoming.ProfileId ??= settings.ActiveProfileId;
            return settings;
        }
        catch (JsonException)
        {
            // Archivo dañado: se conserva una copia y se arranca con valores por defecto.
            File.Copy(settingsFile, settingsFile + ".corrupt", overwrite: true);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
        {
            // Antes de migrar se conserva la configuración anterior para poder volver a la versión previa.
            var backup = BackupFileFor(settings.SchemaVersion);
            if (File.Exists(settingsFile) && !File.Exists(backup))
            {
                File.Copy(settingsFile, backup);
            }

            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        }

        settings.Incoming ??= new IncomingSettings();
        var temp = settingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, settingsFile, overwrite: true);
    }

    public string BackupFileFor(int schemaVersion) =>
        Path.Combine(Path.GetDirectoryName(settingsFile)!, $"settings.v{schemaVersion}.backup.json");
}
