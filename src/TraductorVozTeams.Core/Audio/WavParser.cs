using System.Buffers.Binary;
using System.Text;

namespace TraductorVozTeams.Core.Audio;

/// <summary>
/// Interpreta el audio sintetizado devuelto por el proveedor: puede venir como WAV (RIFF)
/// o como PCM sin cabecera. La documentación no fija el formato, por eso se detecta.
/// </summary>
public static class WavParser
{
    private const ushort PcmFormatTag = 1;

    public static bool IsCompleteWave(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsRiffWave(data))
        {
            return false;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        return size >= 4 && size == data.Length - 8;
    }

    public static AudioChunk Parse(byte[] data, PcmFormat rawFallback)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(rawFallback);

        if (!IsRiffWave(data))
        {
            return new AudioChunk(data, rawFallback);
        }

        PcmFormat? format = null;
        var position = 12;
        while (position + 8 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4, 4));
            var bodyStart = position + 8;
            var available = data.Length - bodyStart;

            if (id == "fmt ")
            {
                if (size < 16 || available < 16)
                {
                    throw new InvalidDataException("Cabecera WAV 'fmt ' incompleta.");
                }

                var body = data.AsSpan(bodyStart, 16);
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (tag != PcmFormatTag)
                {
                    throw new NotSupportedException($"Formato WAV no soportado (código {tag}); se esperaba PCM.");
                }

                format = new PcmFormat(sampleRate, bits, channels);
            }
            else if (id == "data")
            {
                if (format is null)
                {
                    throw new InvalidDataException("El bloque 'data' aparece antes de 'fmt '.");
                }

                // En streaming el tamaño puede ser 0 o 0xFFFFFFFF: se toma el resto.
                var length = size == 0 || size > available ? available : (int)size;
                return new AudioChunk(data.AsSpan(bodyStart, length).ToArray(), format);
            }

            if (size > int.MaxValue - bodyStart)
            {
                break;
            }

            position = bodyStart + (int)size + (int)(size % 2);
        }

        return new AudioChunk([], format ?? rawFallback);
    }

    private static bool IsRiffWave(byte[] data) =>
        data.Length >= 12
        && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
        && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E';
}
