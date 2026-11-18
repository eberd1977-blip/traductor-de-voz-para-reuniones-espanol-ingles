using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.Core.Devices;

public sealed record IncomingRouting(
    AudioDeviceInfo? MeetingOutput,
    AudioDeviceInfo? Capture,
    AudioDeviceInfo? Listen,
    CablePairing? VerifiedPairing);

/// <summary>
/// Reglas de la dirección entrante. Junto con la ruta saliente evita bucles:
/// el cable B no puede compartir extremos con el cable A, y el español solo sale por auriculares.
/// </summary>
public static class IncomingRoutingValidator
{
    private static readonly string[] SpeakerHints = ["Altavoz", "Altavoces", "Speaker", "Speakers"];

    public static RoutingValidationResult Validate(IncomingRouting incoming, DeviceRouting outgoing, IEnumerable<string> virtualCablePatterns)
    {
        var patterns = virtualCablePatterns.ToList();
        var errors = new List<string>();
        var warnings = new List<string>();

        if (incoming.MeetingOutput is null)
        {
            errors.Add("Selecciona el extremo de reproducción del cable para la reunión (el altavoz que elegirás en Teams, Zoom o Meet).");
        }
        else if (outgoing.TeamsOutput is not null && incoming.MeetingOutput.Id == outgoing.TeamsOutput.Id)
        {
            errors.Add("El altavoz de la reunión no puede ser la misma salida que envía tu traducción al micrófono de la reunión. Usa otra línea del cable virtual.");
        }

        if (incoming.Capture is null)
        {
            errors.Add("Selecciona el extremo de grabación del cable por el que llega el audio de la reunión.");
        }
        else
        {
            if (outgoing.Input is not null && incoming.Capture.Id == outgoing.Input.Id)
            {
                errors.Add("La entrada del audio de la reunión no puede ser tu micrófono físico.");
            }

            if (!DeviceRoutingValidator.IsVirtualCable(incoming.Capture, patterns))
            {
                warnings.Add($"La entrada \"{incoming.Capture.Name}\" no parece un cable virtual. Verifícala con «Comprobar cables».");
            }
        }

        if (incoming.Listen is null)
        {
            errors.Add("Selecciona los auriculares donde escucharás el español.");
        }
        else
        {
            if (DeviceRoutingValidator.IsVirtualCable(incoming.Listen, patterns))
            {
                errors.Add($"Los auriculares \"{incoming.Listen.Name}\" parecen un cable virtual. El español debe salir por auriculares para no volver a la reunión.");
            }

            if ((outgoing.TeamsOutput is not null && incoming.Listen.Id == outgoing.TeamsOutput.Id)
                || (incoming.MeetingOutput is not null && incoming.Listen.Id == incoming.MeetingOutput.Id))
            {
                errors.Add("Los auriculares deben ser distintos de las salidas del cable virtual.");
            }

            if (SpeakerHints.Any(h => incoming.Listen.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"\"{incoming.Listen.Name}\" parece un altavoz. Si tu micrófono lo capta, la reunión oirá el español: usa auriculares.");
            }

            if (outgoing.MonitorEnabled && outgoing.Monitor is not null && outgoing.Monitor.Id == incoming.Listen.Id)
            {
                warnings.Add("El monitor de tu traducción al inglés usa los mismos auriculares: oirás ambas traducciones mezcladas.");
            }
        }

        if (incoming.MeetingOutput is not null && incoming.Capture is not null
            && (incoming.VerifiedPairing is null
                || !incoming.VerifiedPairing.Matches(incoming.MeetingOutput.Id, incoming.Capture.Id, outgoing.TeamsOutput?.Id)))
        {
            errors.Add("Ejecuta «Comprobar cables» para verificar con sonido que la salida y la entrada elegidas son el mismo cable.");
        }

        return new RoutingValidationResult(errors, warnings);
    }
}
