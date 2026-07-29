using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Quill.Audio;

/// Captures everything the machine plays (the other side of the call) with a
/// WASAPI loopback client on the default render device. No virtual cable, no
/// driver install, no elevation — the same "just works" property the macOS
/// build gets from Core Audio process taps.
internal sealed class SystemAudioRecorder
{
    private WasapiLoopbackCapture? _capture;
    private TrackWriter? _writer;

    public DateTime? FirstBufferAt => _writer?.FirstBufferAt;

    public void Start(string path)
    {
        using var devices = new MMDeviceEnumerator();
        if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
            throw new InvalidOperationException("no default output device — loopback capture needs one");

        var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var capture = new WasapiLoopbackCapture(device);
        var writer = new TrackWriter(path, capture.WaveFormat);

        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, e.BytesRecorded);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                Console.Error.WriteLine($"system audio capture stopped: {e.Exception.Message}");
        };

        try
        {
            capture.StartRecording();
        }
        catch
        {
            writer.Stop();
            capture.Dispose();
            throw;
        }

        _capture = capture;
        _writer = writer;
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        _writer?.Stop();
    }
}
