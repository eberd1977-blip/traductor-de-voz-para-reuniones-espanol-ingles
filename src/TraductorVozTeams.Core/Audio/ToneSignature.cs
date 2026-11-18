namespace TraductorVozTeams.Core.Audio;

public sealed record ToneDetection(bool Detected, int TonesFound, double PeakDbfs);

/// <summary>
/// Secuencia aleatoria de tonos para comprobar con sonido que una salida y una entrada
/// son extremos del mismo cable virtual, sin depender del nombre de los dispositivos.
/// </summary>
public sealed class ToneSignature
{
    public const int SampleRate = 16000;
    public const int ToneMs = 250;
    public const int GapMs = 50;
    public const double Amplitude = 0.2;
    private const int FrameSamples = SampleRate / 50; // 20 ms; resolución de 50 Hz
    private const int MinFramesPerTone = 6;
    private const double DominanceThreshold = 0.5;
    private const double MinFrameDbfs = -60;

    public ToneSignature(IReadOnlyList<int> frequencies)
    {
        if (frequencies.Count == 0)
        {
            throw new ArgumentException("Se necesita al menos un tono.", nameof(frequencies));
        }

        Frequencies = frequencies;
    }

    public IReadOnlyList<int> Frequencies { get; }

    public static PcmFormat Format { get; } = new(SampleRate, 16, 1);

    public TimeSpan Duration => TimeSpan.FromMilliseconds(Frequencies.Count * (ToneMs + GapMs));

    /// <summary>Tres tonos entre 700 y 3000 Hz, múltiplos de 50 Hz y separados al menos 200 Hz.</summary>
    public static ToneSignature CreateRandom(Random random, int count = 3)
    {
        var chosen = new List<int>();
        while (chosen.Count < count)
        {
            var f = random.Next(700 / 50, 3000 / 50 + 1) * 50;
            if (chosen.All(c => Math.Abs(c - f) >= 200))
            {
                chosen.Add(f);
            }
        }

        return new ToneSignature(chosen);
    }

    public byte[] Render()
    {
        var toneSamples = SampleRate * ToneMs / 1000;
        var gapSamples = SampleRate * GapMs / 1000;
        var fade = SampleRate * 10 / 1000;
        var pcm = new byte[Frequencies.Count * (toneSamples + gapSamples) * 2];
        var pos = 0;
        foreach (var f in Frequencies)
        {
            for (var n = 0; n < toneSamples; n++)
            {
                var envelope = Math.Min(1.0, Math.Min(n, toneSamples - 1 - n) / (double)fade);
                var value = (short)(Amplitude * envelope * short.MaxValue * Math.Sin(2 * Math.PI * f * n / SampleRate));
                pcm[pos++] = (byte)value;
                pcm[pos++] = (byte)(value >> 8);
            }

            pos += gapSamples * 2;
        }

        return pcm;
    }

    /// <summary>Busca los tonos en orden dentro de audio PCM 16 kHz, 16 bit, mono.</summary>
    public ToneDetection Detect(ReadOnlySpan<byte> pcm)
    {
        var samples = new double[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / (double)short.MaxValue;
        }

        var peak = 0.0;
        var labels = new List<int>();
        for (var start = 0; start + FrameSamples <= samples.Length; start += FrameSamples)
        {
            var frame = samples.AsSpan(start, FrameSamples);
            var energy = 0.0;
            foreach (var s in frame)
            {
                energy += s * s;
                peak = Math.Max(peak, Math.Abs(s));
            }

            var rms = Math.Sqrt(energy / FrameSamples);
            if (rms <= 0 || 20 * Math.Log10(rms) < MinFrameDbfs)
            {
                labels.Add(-1);
                continue;
            }

            var label = -1;
            for (var t = 0; t < Frequencies.Count; t++)
            {
                var ratio = GoertzelPower(frame, Frequencies[t]) / (FrameSamples / 2.0 * energy);
                if (ratio >= DominanceThreshold)
                {
                    label = t;
                    break;
                }
            }

            labels.Add(label);
        }

        var found = CountInOrder(labels);
        var peakDbfs = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        return new ToneDetection(found == Frequencies.Count, found, peakDbfs);
    }

    private int CountInOrder(List<int> labels)
    {
        var expected = 0;
        var run = 0;
        foreach (var label in labels)
        {
            if (expected >= Frequencies.Count)
            {
                break;
            }

            run = label == expected ? run + 1 : 0;
            if (run >= MinFramesPerTone)
            {
                expected++;
                run = 0;
            }
        }

        return expected;
    }

    private static double GoertzelPower(ReadOnlySpan<double> frame, int frequency)
    {
        var coeff = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
        double s1 = 0, s2 = 0;
        foreach (var x in frame)
        {
            var s0 = x + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }
}
