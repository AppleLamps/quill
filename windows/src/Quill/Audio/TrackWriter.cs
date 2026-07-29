using System.Diagnostics;
using NAudio.Wave;

namespace Quill.Audio;

/// Streams one capture device into a 16-bit PCM mono WAV file.
///
/// Two behaviours matter here:
///
/// * The header is rewritten on every flush, so a session killed mid-meeting
///   still leaves a playable file — the reason the macOS build writes CAF.
/// * WASAPI hands us nothing while a device is silent (loopback on an idle
///   speaker can go quiet for minutes). Wall-clock gaps larger than
///   `GapTolerance` are filled with silence, so file position stays a
///   faithful clock and the two tracks stay in sync with each other.
internal sealed class TrackWriter : IDisposable
{
    private static readonly TimeSpan GapTolerance = TimeSpan.FromMilliseconds(120);

    private readonly WaveFileWriter _writer;
    private readonly WaveFormat _source;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private readonly int _sourceChannels;
    private readonly int _sampleRate;
    private byte[] _silence = [];
    private bool _disposed;

    public TrackWriter(string path, WaveFormat sourceFormat)
    {
        _source = sourceFormat;
        _sourceChannels = sourceFormat.Channels;
        _sampleRate = sourceFormat.SampleRate;
        _writer = new WaveFileWriter(path, new WaveFormat(_sampleRate, 16, 1));
    }

    /// UTC time the first non-empty buffer arrived, or null if the device
    /// never produced audio. Used to align the two tracks on one clock.
    public DateTime? FirstBufferAt { get; private set; }

    public void Write(ReadOnlySpan<byte> buffer, int count)
    {
        lock (_gate)
        {
            if (_disposed || count == 0) return;
            if (FirstBufferAt is null)
            {
                FirstBufferAt = DateTime.UtcNow;
                _clock.Start();
            }
            else
            {
                PadToWallClock();
            }

            var samples = Decode(buffer[..count]);
            foreach (var sample in samples) _writer.WriteSample(sample);
            _writer.Flush();
        }
    }

    /// Pad the tail so the file length matches the recorded wall-clock span,
    /// then finalize the header.
    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (FirstBufferAt is not null) PadToWallClock();
            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    public void Dispose() => Stop();

    /// Fill the difference between elapsed wall-clock time and audio already
    /// written with silence, once it exceeds the tolerance.
    private void PadToWallClock()
    {
        var missing = _clock.Elapsed - TimeSpan.FromSeconds(
            _writer.Length / (double)(_sampleRate * sizeof(short)));
        if (missing <= GapTolerance) return;

        var frames = (int)(missing.TotalSeconds * _sampleRate);
        var bytes = frames * sizeof(short);
        if (_silence.Length < bytes) _silence = new byte[Math.Max(bytes, _sampleRate * sizeof(short))];
        _writer.Write(_silence, 0, bytes);
    }

    /// Convert an interleaved device buffer to mono float samples. WASAPI
    /// shared mode is float32 in practice, but exclusive-mode and some
    /// drivers hand back packed PCM, so both are handled.
    private float[] Decode(ReadOnlySpan<byte> buffer)
    {
        var bytesPerSample = _source.BitsPerSample / 8;
        var frameSize = bytesPerSample * _sourceChannels;
        if (frameSize == 0) return [];
        var frames = buffer.Length / frameSize;
        var mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < _sourceChannels; channel++)
            {
                var offset = frame * frameSize + channel * bytesPerSample;
                sum += ReadSample(buffer.Slice(offset, bytesPerSample));
            }
            mono[frame] = sum / _sourceChannels;
        }
        return mono;
    }

    private float ReadSample(ReadOnlySpan<byte> sample) =>
        _source.Encoding == WaveFormatEncoding.IeeeFloat
            ? BitConverter.ToSingle(sample)
            : _source.BitsPerSample switch
            {
                16 => BitConverter.ToInt16(sample) / 32768f,
                24 => ((sample[2] << 24 | sample[1] << 16 | sample[0] << 8) >> 8) / 8388608f,
                32 => BitConverter.ToInt32(sample) / 2147483648f,
                8 => (sample[0] - 128) / 128f,
                _ => 0f,
            };
}
