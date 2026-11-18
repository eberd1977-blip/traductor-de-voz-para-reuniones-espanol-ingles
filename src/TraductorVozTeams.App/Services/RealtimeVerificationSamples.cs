using System.Buffers.Binary;
using TraductorVozTeams.App.Providers;

namespace TraductorVozTeams.App.Services;

public static class RealtimeVerificationSamples
{
    public static (byte[] Spanish, byte[] English) Load() => (Load("es"), Load("en"));

    private static byte[] Load(string language)
    {
        var format = RealtimeTranslateProvider.TranslationPcm;
        using var stream = typeof(RealtimeVerificationSamples).Assembly.GetManifestResourceStream($"TraductorVozTeams.Verification.{language}.pcm")
            ?? throw new InvalidDataException("Falta una muestra de voz integrada en la aplicación.");
        if (stream.Length < format.AverageBytesPerSecond / 2 || stream.Length > 6 * format.AverageBytesPerSecond
            || stream.Length % format.BlockAlign != 0)
            throw new InvalidDataException("Una muestra de voz integrada tiene un formato o duración inválidos.");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        if (!HasSignal(data))
            throw new InvalidDataException("Una muestra de voz integrada no contiene una señal audible.");
        return data;
    }

    internal static bool HasSignal(ReadOnlySpan<byte> pcm)
    {
        double energy = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]) / 32768.0;
            energy += sample * sample;
        }
        return pcm.Length > 0 && energy / (pcm.Length / 2.0) > 0.000004;
    }
}
