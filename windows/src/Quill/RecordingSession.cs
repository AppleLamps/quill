using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quill.Audio;

namespace Quill;

/// One meeting recording: a timestamped folder holding two independent tracks
/// (mic = you, system = them) plus a meta.json written on clean stop. Tracks
/// are separate on purpose — speech models do better on clean single-source
/// audio, and two tracks give free two-party diarization.
internal sealed class RecordingSession
{
    public string Dir { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    private readonly MicRecorder _mic = new();
    private readonly SystemAudioRecorder _system = new();

    /// Create the session folder under `root` (yyyy.MM.dd-HHmm, suffixed on
    /// collision) without starting capture yet.
    public RecordingSession(string root)
    {
        var stamp = StartedAt.ToLocalTime().ToString("yyyy.MM.dd-HHmm", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(root, stamp);
        for (var n = 2; Directory.Exists(candidate); n++)
            candidate = Path.Combine(root, $"{stamp}-{n}");
        Directory.CreateDirectory(candidate);
        Dir = candidate;
    }

    /// Start both tracks. If the mic fails after loopback started, loopback is
    /// torn down so we never run half a session silently.
    public void Start()
    {
        _system.Start(Path.Combine(Dir, "system.wav"));
        try
        {
            _mic.Start(Path.Combine(Dir, "mic.wav"));
        }
        catch
        {
            _system.Stop();
            throw;
        }
    }

    /// Stop both tracks and write meta.json. A device that fails to shut down
    /// cleanly must not cost us the other track or the metadata — without
    /// meta.json the session is invisible to the transcription queue.
    public void Stop()
    {
        StopTrack("mic", _mic.Stop);
        StopTrack("system audio", _system.Stop);

        var ended = DateTime.UtcNow;
        // The tracks don't start on the same buffer; record how far each lags
        // the earliest so transcript timestamps share one clock.
        var micStart = _mic.FirstBufferAt ?? StartedAt;
        var systemStart = _system.FirstBufferAt ?? StartedAt;
        var earliest = micStart < systemStart ? micStart : systemStart;

        var meta = new JsonObject
        {
            ["started"] = Iso(StartedAt),
            ["ended"] = Iso(ended),
            ["duration_seconds"] = (int)(ended - StartedAt).TotalSeconds,
            ["files"] = new JsonObject { ["mic"] = "mic.wav", ["system"] = "system.wav" },
            ["start_offset_ms"] = new JsonObject
            {
                ["mic"] = (int)(micStart - earliest).TotalMilliseconds,
                ["system"] = (int)(systemStart - earliest).TotalMilliseconds,
            },
        };

        var json = meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Dir, "meta.json"), json);
    }

    private static void StopTrack(string label, Action stop)
    {
        try
        {
            stop();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{label} track didn't stop cleanly: {e.Message}");
        }
    }

    private static string Iso(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
