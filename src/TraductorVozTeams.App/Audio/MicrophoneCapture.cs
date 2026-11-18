using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TraductorVozTeams.Core.Audio;

namespace TraductorVozTeams.App.Audio;

/// <summary>
/// Captura el micrófono físico con WASAPI y lo entrega convertido al formato del proveedor
/// (16 kHz, 16 bit, mono). Este audio solo va al proveedor, nunca a la salida de Teams.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _converted;
    private readonly byte[] _readBuffer;

    public MicrophoneCapture(MMDevice device, PcmFormat target)
    {
        if (target.BitsPerSample != 16)
        {
            throw new NotSupportedException("Solo se admite salida PCM de 16 bit hacia el proveedor.");
        }

        _capture = new WasapiCapture(device, true, 50);
        _buffer = new BufferedWaveProvider(SampleProviders.Normalize(_capture.WaveFormat))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(5),
        };

        ISampleProvider samples = _buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new DownmixToMonoSampleProvider(samples);
        }

        if (samples.WaveFormat.SampleRate != target.SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, target.SampleRate);
        }

        _converted = new SampleToWaveProvider16(samples);
        _readBuffer = new byte[target.AverageBytesPerSecond / 5];
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    public string SourceFormatDescription => _capture.WaveFormat.ToString();

    /// <summary>Audio convertido listo para el proveedor (buffer reutilizable: copiar si se guarda).</summary>
    public event Action<byte[], int>? DataReady;

    /// <summary>Nivel de pico 0..1 del último bloque.</summary>
    public event Action<float>? LevelChanged;

    public event Action<Exception?>? Stopped;

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        if (_capture.CaptureState != CaptureState.Stopped)
        {
            _capture.StopRecording();
        }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        int read;
        var peak = 0;
        while ((read = _converted.Read(_readBuffer, 0, _readBuffer.Length)) > 0)
        {
            for (var i = 0; i + 1 < read; i += 2)
            {
                var sample = Math.Abs((int)BitConverter.ToInt16(_readBuffer, i));
                if (sample > peak)
                {
                    peak = sample;
                }
            }

            DataReady?.Invoke(_readBuffer, read);
        }

        LevelChanged?.Invoke(Math.Min(1f, peak / 32768f));
    }
}
