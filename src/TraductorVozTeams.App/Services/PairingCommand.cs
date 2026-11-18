using System.IO;
using System.Text;
using TraductorVozTeams.App.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App.Services;

/// <summary>
/// <c>TraductorVozTeams.exe --pairing-test [informe.txt] [--salida "texto"] [--entrada "texto"] [--guardar]</c>.
/// Usa los dispositivos guardados de la dirección entrante, o los que contengan el texto indicado.
/// Con <c>--guardar</c> registra el emparejamiento verificado en la configuración (y los dispositivos usados).
/// Hacerla sin reunión activa: reproduce tonos breves en los cables.
/// </summary>
public static class PairingCommand
{
    public static int Run(string? reportPath, IReadOnlyList<string>? args = null)
    {
        args ??= Environment.GetCommandLineArgs();
        reportPath ??= Path.Combine(AppPaths.Default().Root, $"cables-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var report = new StringBuilder();
        var exitCode = 0;
        try
        {
            var services = new AppServices(AppPaths.Default());
            var settings = services.Settings;
            var (inputs, outputs) = Task.Run(() => (AudioDeviceService.List(AudioDeviceFlow.Capture), AudioDeviceService.List(AudioDeviceFlow.Render)))
                .GetAwaiter().GetResult();

            var meeting = Pick(outputs, Option(args, "--salida"), settings.Incoming.MeetingOutputDeviceId)
                          ?? throw new InvalidOperationException("Falta la salida de la reunión (configúrala o usa --salida \"texto\").");
            var capture = Pick(inputs, Option(args, "--entrada"), settings.Incoming.CaptureDeviceId)
                          ?? throw new InvalidOperationException("Falta la entrada del cable (configúrala o usa --entrada \"texto\").");
            var outgoing = outputs.FirstOrDefault(d => d.Id == settings.TeamsOutputDeviceId);
            var mic = inputs.FirstOrDefault(d => d.Id == settings.InputDeviceId);

            report.AppendLine($"Salida de la reunión (prueba positiva): {meeting.Name}");
            report.AppendLine($"Entrada del cable entrante:            {capture.Name}");
            report.AppendLine($"Salida de la traducción al inglés:     {outgoing?.Name ?? "(no configurada; sin prueba de separación)"}");
            report.AppendLine($"Micrófono físico:                      {mic?.Name ?? "(no configurado)"}");

            var result = CablePairingTest.RunAsync(meeting.Id, capture.Id, outgoing?.Id, mic?.Id, services.Logger).GetAwaiter().GetResult();
            report.AppendLine();
            report.AppendLine(result.Success ? "RESULTADO: OK" : "RESULTADO: FALLO");
            report.AppendLine(result.Message);
            report.AppendLine($"Tonos detectados en la entrada: {result.Positive.TonesFound}/3 (pico {result.Positive.PeakDbfs:0.0} dBFS)");
            if (result.OutgoingLeak is { } leak)
            {
                report.AppendLine($"Fuga desde la salida en inglés hacia la entrada: {(leak.Detected ? "SÍ" : "no")} ({leak.TonesFound}/3)");
            }

            if (result.MicrophoneLeak is { } micLeak)
            {
                report.AppendLine($"Tonos captados por el micrófono físico: {(micLeak.Detected ? "SÍ" : "no")} ({micLeak.TonesFound}/3)");
            }

            if (args.Any(a => a.Equals("--guardar", StringComparison.OrdinalIgnoreCase)))
            {
                settings.Incoming.MeetingOutputDeviceId = meeting.Id;
                settings.Incoming.CaptureDeviceId = capture.Id;
                settings.Incoming.VerifiedPairing = result.Pairing;
                services.SettingsStore.Save(settings);
                report.AppendLine("Configuración guardada.");
            }

            exitCode = result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.AppendLine("FALLO: " + ex.Message);
            exitCode = 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
        return exitCode;
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static AudioDeviceInfo? Pick(IReadOnlyList<AudioDeviceInfo> devices, string? text, string? savedId)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var matches = devices.Where(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count switch
            {
                1 => matches[0],
                0 => throw new InvalidOperationException($"Ningún dispositivo contiene \"{text}\"."),
                _ => throw new InvalidOperationException($"\"{text}\" coincide con varios dispositivos: {string.Join(", ", matches.Select(m => m.Name))}"),
            };
        }

        return devices.FirstOrDefault(d => d.Id == savedId);
    }
}

