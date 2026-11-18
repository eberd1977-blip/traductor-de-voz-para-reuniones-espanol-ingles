namespace TraductorVozTeams.Core.Audio;

public sealed record PcmFormat(int SampleRate, int BitsPerSample, int Channels)
{
    /// <summary>Formato que la app envía al proveedor: 16 kHz, 16 bit, mono.</summary>
    public static PcmFormat SpeechInput { get; } = new(16000, 16, 1);

    public int BlockAlign => Channels * BitsPerSample / 8;

    public int AverageBytesPerSecond => SampleRate * BlockAlign;

    public override string ToString() => $"{SampleRate} Hz, {BitsPerSample} bit, {Channels} canal(es)";
}

public sealed record AudioChunk(byte[] Data, PcmFormat Format)
{
    public bool IsEmpty => Data.Length == 0;

    public TimeSpan Duration => Format.AverageBytesPerSecond == 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)Data.Length / Format.AverageBytesPerSecond);
}
