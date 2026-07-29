using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Quill.Audio;

/// Streams one capture device into a 16-bit PCM mono WAV file.
///
/// Two behaviours matter here:
///
/// * The RIFF header is rewritten roughly once a second, so a session killed
///   mid-meeting still leaves a playable file — the property the macOS build
///   gets for free from CAF. Rewriting it on every buffer would mean two
///   seeks and a flush every ~10 ms per track, on the capture thread.
/// * WASAPI hands us nothing while a device is silent (loopback on an idle
///   speaker can go quiet for minutes). Wall-clock gaps larger than
///   `GapTolerance` are filled with silence, so file position stays a
///   faithful clock and the two tracks stay in sync with each other.
internal sealed class TrackWriter : IDisposable
{
    private static readonly TimeSpan GapTolerance = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan HeaderInterval = TimeSpan.FromSeconds(1);

    private readonly WaveFileWriter _writer;
    private readonly WaveFormat _source;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private readonly int _sourceChannels;
    private readonly int _sampleRate;
    private readonly int _bytesPerSample;
    private byte[] _pcm = [];
    private byte[] _silence = [];
    private TimeSpan _lastHeaderWrite;
    private bool _stopped;

    public TrackWriter(string path, WaveFormat sourceFormat)
    {
        _source = sourceFormat;
        _sourceChannels = Math.Max(1, sourceFormat.Channels);
        _sampleRate = sourceFormat.SampleRate;
        _bytesPerSample = sourceFormat.BitsPerSample / 8;
        _writer = new WaveFileWriter(path, new WaveFormat(_sampleRate, 16, 1));
    }

    /// UTC time the first non-empty buffer arrived, or null if the device
    /// never produced audio. Used to align the two tracks on one clock.
    public DateTime? FirstBufferAt { get; private set; }

    /// Append one device buffer. Called on the capture thread; buffers that
    /// arrive after Stop() (WASAPI drains asynchronously) are dropped.
    public void Write(ReadOnlySpan<byte> buffer, int count)
    {
        lock (_gate)
        {
            if (_stopped || count <= 0) return;
            if (FirstBufferAt is null)
            {
                FirstBufferAt = DateTime.UtcNow;
                _clock.Start();
            }
            else
            {
                PadToWallClock();
            }

            var bytes = Decode(buffer[..count]);
            if (bytes > 0) _writer.Write(_pcm, 0, bytes);
            MaybeWriteHeader();
        }
    }

    /// Pad the tail so the file length matches the recorded wall-clock span,
    /// then finalize the header.
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            if (FirstBufferAt is not null) PadToWallClock();
            _writer.Flush();
            _writer.Dispose();
        }
    }

    public void Dispose() => Stop();

    private void MaybeWriteHeader()
    {
        if (_clock.Elapsed - _lastHeaderWrite < HeaderInterval) return;
        _lastHeaderWrite = _clock.Elapsed;
        _writer.Flush();
    }

    /// Fill the difference between elapsed wall-clock time and audio already
    /// written with silence, once it exceeds the tolerance. Written a second
    /// at a time: an hour of silence is 170 MB, not something to allocate.
    private void PadToWallClock()
    {
        var written = TimeSpan.FromSeconds(_writer.Length / (double)(_sampleRate * sizeof(short)));
        var missing = _clock.Elapsed - written;
        if (missing <= GapTolerance) return;

        if (_silence.Length == 0) _silence = new byte[_sampleRate * sizeof(short)];
        var remaining = (long)(missing.TotalSeconds * _sampleRate) * sizeof(short);
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, _silence.Length);
            _writer.Write(_silence, 0, chunk);
            remaining -= chunk;
        }
    }

    /// Convert an interleaved device buffer into `_pcm` as 16-bit mono,
    /// returning the byte count. WASAPI shared mode is float32 in practice,
    /// but exclusive mode and some drivers hand back packed PCM, so both are
    /// handled. Sample-at-a-time writes through WaveFileWriter would cost a
    /// virtual call and a bounds check per sample on the capture thread.
    private int Decode(ReadOnlySpan<byte> buffer)
    {
        var frameSize = _bytesPerSample * _sourceChannels;
        if (frameSize == 0) return 0;
        var frames = buffer.Length / frameSize;
        var bytes = frames * sizeof(short);
        if (_pcm.Length < bytes) _pcm = new byte[bytes];
        var output = MemoryMarshal.Cast<byte, short>(_pcm.AsSpan(0, bytes));

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < _sourceChannels; channel++)
            {
                var offset = frame * frameSize + channel * _bytesPerSample;
                sum += ReadSample(buffer.Slice(offset, _bytesPerSample));
            }
            // Clamp: a downmix of correlated channels, or a float source with
            // headroom above 1.0, would otherwise wrap to full-scale noise.
            var value = Math.Clamp(sum / _sourceChannels, -1f, 1f);
            output[frame] = (short)(value * short.MaxValue);
        }
        return bytes;
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
