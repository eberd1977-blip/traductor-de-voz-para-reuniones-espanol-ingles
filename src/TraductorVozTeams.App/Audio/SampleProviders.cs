using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TraductorVozTeams.App.Audio;

internal static class SampleProviders
{
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Convierte formatos WAVE_FORMAT_EXTENSIBLE (habituales en WASAPI) a PCM o float simples.</summary>
    public static WaveFormat Normalize(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible)
        {
            return format;
        }

        var isFloat = format is WaveFormatExtensible ext
            ? ext.SubFormat == IeeeFloatSubtype
            : format.BitsPerSample == 32;
        return isFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)
            : new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
    }
}

/// <summary>Mezcla N canales a mono promediando.</summary>
internal sealed class DownmixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buffer = [];

    public DownmixToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var needed = count * _channels;
        if (_buffer.Length < needed)
        {
            _buffer = new float[needed];
        }

        var read = _source.Read(_buffer, 0, needed);
        var frames = read / _channels;
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < _channels; c++)
            {
                sum += _buffer[(i * _channels) + c];
            }

            buffer[offset + i] = sum / _channels;
        }

        return frames;
    }
}

/// <summary>Copia una señal mono a todos los canales del dispositivo de salida.</summary>
internal sealed class MonoToMultiChannelSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buffer = [];

    public MonoToMultiChannelSampleProvider(ISampleProvider source, int channels)
    {
        _source = source;
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / _channels;
        if (_buffer.Length < frames)
        {
            _buffer = new float[frames];
        }

        var read = _source.Read(_buffer, 0, frames);
        for (var i = 0; i < read; i++)
        {
            for (var c = 0; c < _channels; c++)
            {
                buffer[offset + (i * _channels) + c] = _buffer[i];
            }
        }

        return read * _channels;
    }
}
