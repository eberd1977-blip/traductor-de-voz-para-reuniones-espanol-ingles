using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TraductorVozTeams.Core.Audio;

namespace TraductorVozTeams.App.Audio;

/// <summary>
/// Reproduce el audio traducido en un dispositivo concreto (extremo de reproducción de
/// Virtual Audio Cable o auriculares). Emite silencio cuando no hay traducción.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    private readonly MMDevice _device;
    private readonly object _gate = new();
    private float _volume;
    private PcmFormat _format;
    private BufferedWaveProvider _buffer = null!;
    private VolumeSampleProvider _volumeProvider = null!;
    private WasapiOut _player = null!;

    public AudioOutput(MMDevice device, PcmFormat initialFormat, float volume)
    {
        _device = device;
        _volume = volume;
        _format = initialFormat;
        Build();
    }

    public string DeviceName => _device.FriendlyName;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            lock (_gate)
            {
                _volumeProvider.Volume = _volume;
            }
        }
    }

    public void Enqueue(AudioChunk chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (chunk.Format != _format)
            {
                _player.Stop();
                _player.Dispose();
                _format = chunk.Format;
                Build();
            }

            _buffer.AddSamples(chunk.Data, 0, chunk.Data.Length);
        }
    }

    /// <summary>Audio pendiente de reproducir.</summary>
    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_gate)
            {
                return _buffer.BufferedDuration;
            }
        }
    }

    /// <summary>Descarta el audio pendiente (p. ej. al silenciar o si se acumula retraso).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _buffer.ClearBuffer();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _player.Stop();
            _player.Dispose();
        }

        _device.Dispose();
    }

    private void Build()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(_format.SampleRate, _format.BitsPerSample, _format.Channels))
        {
            BufferDuration = TimeSpan.FromMinutes(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        var mix = _device.AudioClient.MixFormat;
        ISampleProvider samples = _buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new DownmixToMonoSampleProvider(samples);
        }

        if (samples.WaveFormat.SampleRate != mix.SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);
        }

        samples = new MonoToMultiChannelSampleProvider(samples, mix.Channels);
        _volumeProvider = new VolumeSampleProvider(samples) { Volume = _volume };
        _player = new WasapiOut(_device, AudioClientShareMode.Shared, true, 100);
        _player.Init(_volumeProvider);
        _player.Play();
    }
}
