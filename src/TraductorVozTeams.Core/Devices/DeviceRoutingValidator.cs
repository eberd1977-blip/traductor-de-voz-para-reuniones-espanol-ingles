namespace TraductorVozTeams.Core.Devices;

public enum AudioDeviceFlow
{
    Capture,
    Render,
}

public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceFlow Flow)
{
    public override string ToString() => Name;
}

public sealed record DeviceRouting(
    AudioDeviceInfo? Input,
    AudioDeviceInfo? TeamsOutput,
    bool MonitorEnabled,
    AudioDeviceInfo? Monitor);

public sealed record RoutingValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Reglas para evitar bucles de audio: la app nunca captura desde un cable virtual
/// y el monitor nunca coincide con la salida hacia Teams ni es un cable virtual.
/// </summary>
public static class DeviceRoutingValidator
{
    public static IReadOnlyList<string> DefaultVirtualCablePatterns { get; } =
        ["Virtual Audio Cable", "Virtual Cable", "VB-Audio"];

    public static bool IsVirtualCable(AudioDeviceInfo device, IEnumerable<string> patterns) =>
        patterns.Any(p => !string.IsNullOrWhiteSpace(p)
            && device.Name.Contains(p.Trim(), StringComparison.OrdinalIgnoreCase));

    public static RoutingValidationResult Validate(DeviceRouting routing, IEnumerable<string> virtualCablePatterns)
    {
        var patterns = virtualCablePatterns.ToList();
        var errors = new List<string>();
        var warnings = new List<string>();

        if (routing.Input is null)
        {
            errors.Add("Selecciona el micrófono físico de entrada.");
        }
        else if (IsVirtualCable(routing.Input, patterns))
        {
            errors.Add($"La entrada \"{routing.Input.Name}\" parece un cable virtual. Usa el micrófono físico para evitar bucles de audio.");
        }

        if (routing.TeamsOutput is null)
        {
            errors.Add("Selecciona la salida hacia Teams (extremo de reproducción de Virtual Audio Cable).");
        }
        else if (!IsVirtualCable(routing.TeamsOutput, patterns))
        {
            warnings.Add($"La salida a Teams \"{routing.TeamsOutput.Name}\" no parece un cable virtual. Teams solo la recibirá si es un extremo de Virtual Audio Cable.");
        }

        if (routing.MonitorEnabled)
        {
            if (routing.Monitor is null)
            {
                errors.Add("Selecciona los auriculares de monitor o desactiva el monitor.");
            }
            else
            {
                if (routing.TeamsOutput is not null && routing.Monitor.Id == routing.TeamsOutput.Id)
                {
                    errors.Add("El monitor y la salida a Teams deben ser dispositivos distintos.");
                }
                else if (IsVirtualCable(routing.Monitor, patterns))
                {
                    errors.Add($"El monitor \"{routing.Monitor.Name}\" parece un cable virtual. Usa auriculares para evitar bucles.");
                }
            }
        }

        return new RoutingValidationResult(errors, warnings);
    }
}
