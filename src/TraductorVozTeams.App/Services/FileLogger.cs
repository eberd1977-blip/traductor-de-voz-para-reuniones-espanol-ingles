using System.Text;

namespace TraductorVozTeams.App.Services;

/// <summary>Registro local sencillo. Nunca se registran claves ni credenciales.</summary>
public sealed class FileLogger(string directory)
{
    private readonly object _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // El registro nunca debe interrumpir la traducción.
        }
    }
}
