using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Quill.Audio;

/// Captures everything the machine plays (the other side of the call) with a
/// WASAPI loopback client on the default render device. No virtual cable, no
/// driver install, no elevation — the same "just works" property the macOS
/// build gets from Core Audio process taps.
internal sealed class SystemAudioRecorder : CaptureTrack
{
    protected override string Label => "system audio";

    protected override MMDevice ResolveDevice(MMDeviceEnumerator devices)
    {
        if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
            throw new InvalidOperationException("no default output device — loopback capture needs one");
        return devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }

    protected override IWaveIn CreateCapture(MMDevice device) => new WasapiLoopbackCapture(device);
}
