using TraductorVozTeams.App.Audio;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App.Services;

public sealed record PairingTestResult(
    bool Success,
    string Message,
    ToneDetection Positive,
    ToneDetection? OutgoingLeak,
    ToneDetection? MicrophoneLeak,
    CablePairing? Pairing);

/// <summary>
/// Prueba de sonido que verifica, por ID de dispositivo, que la salida elegida para la reunión
/// y la entrada de la dirección entrante son extremos del mismo cable, y que ese cable está
/// separado del cable de la dirección saliente. Los tonos son aleatorios en cada prueba.
/// Hacerla sin reunión activa: la prueba de separación suena en el cable del micrófono de la reunión.
/// </summary>
public static class CablePairingTest
{
    private static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(900);
    private const int PositiveAttempts = 3;

    public static Task<PairingTestResult> RunAsync(
        string meetingOutputId,
        string captureId,
        string? outgoingOutputId,
        string? physicalMicId,
        FileLogger? logger,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var random = new Random();
        var captures = new List<string> { captureId };
        if (physicalMicId is not null && physicalMicId != captureId)
        {
            captures.Add(physicalMicId);
        }

        // Otro programa puede estar sonando en el mismo cable y tapar un tono; se reintenta con una firma nueva.
        ToneSignature signature;
        ToneDetection positive;
        ToneDetection? micLeak = null;
        var attempts = 0;
        do
        {
            attempts++;
            signature = ToneSignature.CreateRandom(random);
            var heard = await PlayAndCaptureAsync(meetingOutputId, signature, captures, cancellationToken).ConfigureAwait(false);
            positive = signature.Detect(heard[0]);
            if (captures.Count > 1)
            {
                var leak = signature.Detect(heard[1]);
                micLeak = micLeak is { Detected: true } ? micLeak : leak;
            }
        }
        while (!positive.Detected && attempts < PositiveAttempts);

        ToneDetection? outgoingLeak = null;
        if (outgoingOutputId is not null && outgoingOutputId != meetingOutputId)
        {
            ToneSignature other;
            do
            {
                other = ToneSignature.CreateRandom(random);
            }
            while (other.Frequencies.SequenceEqual(signature.Frequencies));

            var heardOther = await PlayAndCaptureAsync(outgoingOutputId, other, [captureId], cancellationToken).ConfigureAwait(false);
            outgoingLeak = other.Detect(heardOther[0]);
        }

        var problems = new List<string>();
        if (!positive.Detected)
        {
            problems.Add($"La entrada no recibió los tonos en {attempts} intentos ({positive.TonesFound}/{signature.Frequencies.Count}; pico {Db(positive.PeakDbfs)}). " +
                         "No son el mismo cable, la línea está silenciada o con volumen bajo, u otro programa está reproduciendo sonido en ese cable.");
        }

        if (outgoingOutputId == meetingOutputId)
        {
            problems.Add("La salida de la reunión coincide con la salida de tu traducción al inglés: usa otra línea del cable.");
        }

        if (outgoingLeak?.Detected == true)
        {
            problems.Add("La entrada también recibe la salida de tu traducción al inglés: ambas direcciones comparten cable y se produciría un bucle.");
        }

        if (micLeak?.Detected == true)
        {
            problems.Add("Tu micrófono físico captó los tonos del cable: revisa la escucha de dispositivos (\"Escuchar este dispositivo\") en Windows.");
        }

        var success = problems.Count == 0;
        var message = success
            ? $"Cable verificado: los tonos ({string.Join(", ", signature.Frequencies)} Hz) llegaron a la entrada (pico {Db(positive.PeakDbfs)}) y no se detectó cruce con la otra dirección."
            : string.Join(" ", problems);
        logger?.Info($"[cables] {(success ? "OK" : "FALLO")}: intentos={attempts}, positivo={positive.TonesFound}, cruce_saliente={outgoingLeak?.TonesFound.ToString() ?? "-"}, micrófono={micLeak?.TonesFound.ToString() ?? "-"}");

        var pairing = success ? new CablePairing(meetingOutputId, captureId, outgoingOutputId, DateTimeOffset.Now) : null;
        return new PairingTestResult(success, message, positive, outgoingLeak, micLeak, pairing);
    }, cancellationToken);

    private static string Db(double dbfs) => double.IsNegativeInfinity(dbfs) ? "silencio" : $"{dbfs:0} dBFS";

    private static async Task<byte[][]> PlayAndCaptureAsync(string renderId, ToneSignature signature, IReadOnlyList<string> captureIds,
        CancellationToken cancellationToken)
    {
        var streams = captureIds.Select(_ => new MemoryStream()).ToArray();
        var captures = new List<MicrophoneCapture>();
        try
        {
            for (var i = 0; i < captureIds.Count; i++)
            {
                var stream = streams[i];
                var capture = new MicrophoneCapture(AudioDeviceService.Open(captureIds[i]), ToneSignature.Format);
                capture.DataReady += (buffer, count) =>
                {
                    lock (stream)
                    {
                        stream.Write(buffer, 0, count);
                    }
                };
                captures.Add(capture);
                capture.Start();
            }

            using (var output = new AudioOutput(AudioDeviceService.Open(renderId), ToneSignature.Format, 1f))
            {
                await Task.Delay(LeadIn, cancellationToken).ConfigureAwait(false);
                output.Enqueue(new AudioChunk(signature.Render(), ToneSignature.Format));
                await Task.Delay(signature.Duration + Tail, cancellationToken).ConfigureAwait(false);
            }

            foreach (var capture in captures)
            {
                capture.Stop();
            }

            return streams.Select(s =>
            {
                lock (s)
                {
                    return s.ToArray();
                }
            }).ToArray();
        }
        finally
        {
            foreach (var capture in captures)
            {
                capture.Dispose();
            }
        }
    }
}
