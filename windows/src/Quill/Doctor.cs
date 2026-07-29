using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Quill.Transcription;

namespace Quill;

internal abstract record CheckStatus
{
    public sealed record Ok : CheckStatus;
    public sealed record Warn(string Message) : CheckStatus;
    public sealed record Fail(string Message) : CheckStatus;
}

internal sealed record Check(string Name, CheckStatus Status, string? Remediation = null);

internal static class DoctorReport
{
    public static IReadOnlyList<Check> Run(string recordingsRoot) =>
    [
        CheckMicrophone(),
        CheckSystemAudio(),
        CheckRecordingsRoot(recordingsRoot),
        CheckTranscription(),
    ];

    /// Two things can stop the mic track: no capture endpoint, or the Windows
    /// privacy toggle. Both are visible without opening a stream, so unlike
    /// macOS we can report the real state instead of "unknowable".
    private static Check CheckMicrophone()
    {
        MMDevice device;
        try
        {
            using var devices = new MMDeviceEnumerator();
            var role = Config.MicUseCommunicationsDevice() ? Role.Communications : Role.Console;
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Capture, role))
                return new Check("microphone", new CheckStatus.Fail("no default input device"),
                    "plug in a microphone, then Settings → System → Sound → Input");
            device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, role);
        }
        catch (Exception e)
        {
            return new Check("microphone", new CheckStatus.Fail(e.Message));
        }

        var consent = MicrophoneConsent();
        if (consent is false)
            return new Check("microphone", new CheckStatus.Fail("blocked by Windows privacy settings"),
                "Settings → Privacy & security → Microphone → allow desktop apps");

        return new Check("microphone", new CheckStatus.Ok(),
            $"device: {device.FriendlyName}");
    }

    /// WASAPI loopback needs no consent prompt at all — only a render device
    /// to attach to. If the machine has no output, there is nothing to record.
    private static Check CheckSystemAudio()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
                return new Check("system audio", new CheckStatus.Fail("no default output device"),
                    "loopback capture records the default playback device — enable one in Settings → System → Sound");
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return new Check("system audio", new CheckStatus.Ok(), $"loopback on: {device.FriendlyName}");
        }
        catch (Exception e)
        {
            return new Check("system audio", new CheckStatus.Fail(e.Message));
        }
    }

    private static Check CheckRecordingsRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".quill-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return new Check("recordings folder", new CheckStatus.Ok(), root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Check("recordings folder", new CheckStatus.Fail($"{root} is not writable"),
                "check permissions on the directory, or set recordings_dir in the config");
        }
    }

    /// Never discover a missing model after an important meeting: report
    /// whether the whisper weights are already cached.
    private static Check CheckTranscription()
    {
        if (!Config.TranscriptionEnabled())
            return new Check("transcription", new CheckStatus.Warn("disabled in config"));

        // whisper.cpp is a C++ native library: without the VC++ runtime it
        // fails at load time, which would only surface after a meeting. A
        // warning, not a failure — recording still works without it.
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("vcruntime140_1.dll", out _))
            return new Check("transcription",
                new CheckStatus.Warn("Visual C++ runtime missing — whisper can't load"),
                "install https://aka.ms/vs/17/release/vc_redist.x64.exe, or disable transcription in the config");

        var model = Config.TranscriptionModel();
        if (WhisperEngine.ModelIsCached(model))
            return new Check("transcription", new CheckStatus.Ok(),
                $"whisper {model} — {WhisperEngine.ModelPath(model)}");

        return new Check("transcription", new CheckStatus.Warn($"whisper model {model} not downloaded"),
            "downloads automatically on first transcription — record a short test session while online");
    }

    /// HKCU consent for desktop (non-packaged) apps. Absent means "allowed":
    /// the key only appears once the setting has been touched.
    private static bool? MicrophoneConsent()
    {
        const string path =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key?.GetValue("Value") is not string value) return null;
        return value.Equals("Allow", StringComparison.OrdinalIgnoreCase);
    }

    public static void Print(IReadOnlyList<Check> checks)
    {
        foreach (var check in checks)
        {
            var (mark, label) = check.Status switch
            {
                CheckStatus.Ok => ("+", "ok"),
                CheckStatus.Warn w => ("!", w.Message),
                CheckStatus.Fail f => ("x", f.Message),
                _ => ("?", "unknown"),
            };
            Console.WriteLine($"{mark} {check.Name}: {label}");
            if (check.Remediation is { } r) Console.WriteLine($"    -> {r}");
        }
    }

    /// True if no checks are in a hard-fail state. Warnings don't block.
    public static bool AllOk(IReadOnlyList<Check> checks) =>
        checks.All(c => c.Status is not CheckStatus.Fail);
}
