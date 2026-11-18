using System.IO;
using System.Text;
using TraductorVozTeams.App.Providers;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App.Services;

/// <summary>
/// Lista las voces disponibles para el perfil activo: <c>TraductorVozTeams.exe --voices [informe.txt]</c>.
/// Solo consulta el catálogo de voces; no sintetiza audio.
/// </summary>
public static class VoiceListing
{
    public static int Run(string? reportPath)
    {
        reportPath ??= Path.Combine(AppPaths.Default().Root, $"voces-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var report = new StringBuilder();
        var exitCode = 0;
        try
        {
            var services = new AppServices(AppPaths.Default());
            var profile = services.Settings.ActiveProfile ?? throw new InvalidOperationException("No hay perfil activo.");
            if (profile.ProviderType != TraductorVozTeams.Core.Providers.AzureSpeechDescriptor.Type)
                throw new InvalidOperationException("El listado de voces solo se admite con un perfil Azure AI Speech activo.");
            var factory = new AzureSpeechProviderFactory();
            foreach (var (label, context) in new[]
                     {
                         ("saliente (inglés)", services.CreateContext(profile)),
                         ("entrante (español)", services.CreateIncomingContext(profile)),
                     })
            {
                var voices = Task.Run(() => factory.ListVoicesAsync(context, CancellationToken.None)).GetAwaiter().GetResult();
                report.AppendLine($"Perfil: {profile.Name}. Dirección {label}: {voices.Count} voces");
                foreach (var voice in voices)
                {
                    report.AppendLine($"{voice.Name}\t{voice}");
                }

                report.AppendLine();
            }

            var configured = services.Settings.Incoming.VoiceName;
            report.AppendLine($"Voz en español configurada: {configured}");
        }
        catch (Exception ex)
        {
            report.AppendLine("FALLO: " + ex.Message);
            exitCode = 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
        return exitCode;
    }
}
