using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Quill.Audio;

/// Captures the default input device (your side of the call) via WASAPI
/// shared mode. Shared mode on purpose: exclusive mode would take the mic
/// away from the meeting app.
internal sealed class MicRecorder
{
    private WasapiCapture? _capture;
    private TrackWriter? _writer;

    public DateTime? FirstBufferAt => _writer?.FirstBufferAt;

    public void Start(string path)
    {
        var role = Config.MicUseCommunicationsDevice() ? Role.Communications : Role.Console;
        using var devices = new MMDeviceEnumerator();
        if (!devices.HasDefaultAudioEndpoint(DataFlow.Capture, role))
            throw new InvalidOperationException("no default input device — plug in or enable a microphone");

        var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, role);
        var capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
        var writer = new TrackWriter(path, capture.WaveFormat);

        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, e.BytesRecorded);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                Console.Error.WriteLine($"mic capture stopped: {e.Exception.Message}");
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
