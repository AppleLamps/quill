using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Quill.Audio;

/// Shared lifecycle for one WASAPI capture client writing one track: resolve
/// the endpoint, stream it into a `TrackWriter`, and shut down without losing
/// the tail of the recording.
internal abstract class CaptureTrack : IDisposable
{
    /// WASAPI drains asynchronously — StopRecording() returns immediately and
    /// the capture thread delivers what's left before raising RecordingStopped.
    /// Finalizing the WAV before that arrives would drop the last buffers,
    /// which is exactly the part of a meeting people re-listen to.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private readonly ManualResetEventSlim _drained = new(false);
    private IWaveIn? _capture;
    private MMDevice? _device;
    private TrackWriter? _writer;

    public DateTime? FirstBufferAt => _writer?.FirstBufferAt;

    /// Human-readable track name used in error messages ("mic", "system audio").
    protected abstract string Label { get; }

    /// Resolve the endpoint this track records. Throws with an actionable
    /// message when the machine has nothing to record.
    protected abstract MMDevice ResolveDevice(MMDeviceEnumerator devices);

    /// Wrap the endpoint in the right capture client (input vs loopback).
    protected abstract IWaveIn CreateCapture(MMDevice device);

    public void Start(string path)
    {
        // A track restarted on the same instance must wait for the *new*
        // capture's drain, not observe the last one's already-signalled state.
        _drained.Reset();

        using var enumerator = new MMDeviceEnumerator();
        var device = ResolveDevice(enumerator);

        IWaveIn? capture = null;
        TrackWriter? writer = null;
        try
        {
            capture = CreateCapture(device);
            writer = new TrackWriter(path, capture.WaveFormat);
            var sink = writer;

            capture.DataAvailable += (_, e) => sink.Write(e.Buffer, e.BytesRecorded);
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    Console.Error.WriteLine($"{Label} capture stopped: {e.Exception.Message}");
                _drained.Set();
            };

            capture.StartRecording();
        }
        catch
        {
            writer?.Stop();
            capture?.Dispose();
            device.Dispose();
            throw;
        }

        _device = device;
        _capture = capture;
        _writer = writer;
    }

    public void Stop()
    {
        if (_capture is { } capture)
        {
            capture.StopRecording();
            if (!_drained.Wait(DrainTimeout))
                Console.Error.WriteLine($"{Label} capture didn't drain in {DrainTimeout.TotalSeconds:0}s");
            capture.Dispose();
        }
        _writer?.Stop();
        _device?.Dispose();

        _capture = null;
        _writer = null;
        _device = null;
    }

    /// Waiting on `_drained` with a timeout inflates it into a real kernel
    /// event; a daemon that records all day shouldn't leave one per track for
    /// the finalizer to collect.
    public void Dispose()
    {
        Stop();
        _drained.Dispose();
    }
}
