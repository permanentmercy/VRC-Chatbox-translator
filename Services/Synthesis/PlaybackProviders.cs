using System;
using System.Collections.Concurrent;
using NAudio.Wave;

namespace VrcChatboxDemo.Services.Synthesis;

/// <summary>
/// 带有软削顶（Soft Limiter / Tanh 限幅）的平滑音量控制，
/// 杜绝音量过大时产生硬方波杂音与刺耳电音。
/// </summary>
public sealed class SoftLimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _volume;

    public SoftLimiterSampleProvider(ISampleProvider source, float volume)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _volume = Math.Clamp(volume, 0.0f, 2.0f);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int samplesRead = _source.Read(buffer);
        if (Math.Abs(_volume - 1.0f) < 0.001f)
        {
            // 音量为 100% 时，做轻微柔和防削顶保底
            for (int i = 0; i < samplesRead; i++)
            {
                float s = buffer[i];
                if (s > 0.98f) buffer[i] = 0.98f;
                else if (s < -0.98f) buffer[i] = -0.98f;
            }
            return samplesRead;
        }

        for (int i = 0; i < samplesRead; i++)
        {
            float s = buffer[i] * _volume;
            // 超过 0.95 时平滑应用 Tanh 软限幅，避免硬截断产生电音毛刺
            if (s > 0.95f)
            {
                s = 0.95f + 0.049f * (float)Math.Tanh((s - 0.95f) / 0.05f);
            }
            else if (s < -0.95f)
            {
                s = -0.95f - 0.049f * (float)Math.Tanh((-s - 0.95f) / 0.05f);
            }
            buffer[i] = s;
        }

        return samplesRead;
    }
}

/// <summary>
/// 高性能无界流式音频队列 Provider，解除 NAudio 5 秒缓冲限制，
/// 支持任意超长子句与多句连续无缝播放。
/// </summary>
public sealed class SpeechQueueWaveProvider : IWaveProvider
{
    private readonly WaveFormat _waveFormat;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly object _lock = new();
    private byte[]? _currentChunk;
    private int _currentOffset;

    public SpeechQueueWaveProvider(WaveFormat waveFormat)
    {
        _waveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
    }

    public WaveFormat WaveFormat => _waveFormat;

    public void AddSamples(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        byte[] copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        _queue.Enqueue(copy);
    }

    public int BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                int total = (_currentChunk != null) ? (_currentChunk.Length - _currentOffset) : 0;
                foreach (var chunk in _queue)
                {
                    total += chunk.Length;
                }
                return total;
            }
        }
    }

    public void ClearBuffer()
    {
        lock (_lock)
        {
            _currentChunk = null;
            _currentOffset = 0;
            while (_queue.TryDequeue(out _)) { }
        }
    }

    public int Read(Span<byte> buffer)
    {
        int bytesRead = 0;
        int count = buffer.Length;
        lock (_lock)
        {
            while (bytesRead < count)
            {
                if (_currentChunk == null || _currentOffset >= _currentChunk.Length)
                {
                    if (!_queue.TryDequeue(out _currentChunk))
                    {
                        _currentChunk = null;
                        _currentOffset = 0;
                        break;
                    }
                    _currentOffset = 0;
                }

                int toCopy = Math.Min(count - bytesRead, _currentChunk.Length - _currentOffset);
                _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Slice(bytesRead, toCopy));
                _currentOffset += toCopy;
                bytesRead += toCopy;
            }
        }

        // 保持输出填满（静音补齐），防止 WASAPI 出现硬件欠载（Underrun）爆音或断流
        if (bytesRead < count)
        {
            buffer.Slice(bytesRead).Clear();
            bytesRead = count;
        }

        return bytesRead;
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
}
