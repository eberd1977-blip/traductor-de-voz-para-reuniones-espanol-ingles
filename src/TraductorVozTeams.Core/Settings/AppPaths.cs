namespace TraductorVozTeams.Core.Settings;

/// <summary>
/// Carpetas de datos del usuario, separadas del ejecutable:
/// %LOCALAPPDATA%\TraductorVozTeams (o TRADUCTORVOZTEAMS_DATA_DIR si está definida).
/// </summary>
public sealed class AppPaths
{
    public const string AppName = "TraductorVozTeams";
    public const string DataDirOverrideVariable = "TRADUCTORVOZTEAMS_DATA_DIR";

    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SecretsDirectory => Path.Combine(Root, "secrets");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string MetricsDirectory => Path.Combine(Root, "metrics");

    public static AppPaths Default()
    {
        var overrideDir = Environment.GetEnvironmentVariable(DataDirOverrideVariable);
        var root = string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName)
            : overrideDir;
        return new AppPaths(root);
    }

    public static string DefaultInstallDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
}
