using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Quill.Audio;

/// Captures the default input device (your side of the call) via WASAPI
/// shared mode. Shared mode on purpose: exclusive mode would take the mic
/// away from the meeting app.
internal sealed class MicRecorder : CaptureTrack
{
    protected override string Label => "mic";

    protected override MMDevice ResolveDevice(MMDeviceEnumerator devices)
    {
        var role = Config.MicUseCommunicationsDevice() ? Role.Communications : Role.Console;
        if (!devices.HasDefaultAudioEndpoint(DataFlow.Capture, role))
            throw new InvalidOperationException("no default input device — plug in or enable a microphone");
        return devices.GetDefaultAudioEndpoint(DataFlow.Capture, role);
    }

    protected override IWaveIn CreateCapture(MMDevice device) =>
        new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
}
