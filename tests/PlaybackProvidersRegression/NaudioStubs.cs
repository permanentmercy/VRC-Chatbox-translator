namespace NAudio.Wave;

/// <summary>最小 WaveFormat 桩：生产代码只用到采样率/位深/声道与相等性。</summary>
public sealed class WaveFormat
{
    public WaveFormat(int sampleRate, int bits, int channels)
    {
        SampleRate = sampleRate;
        BitsPerSample = bits;
        Channels = channels;
    }

    public int SampleRate { get; }
    public int BitsPerSample { get; }
    public int Channels { get; }
    public int BlockAlign => BitsPerSample / 8 * Channels;
}

public interface IWaveProvider
{
    WaveFormat WaveFormat { get; }
    int Read(byte[] buffer, int offset, int count);
}

public interface ISampleProvider
{
    WaveFormat WaveFormat { get; }
    int Read(Span<float> buffer);
}
