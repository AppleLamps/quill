using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill;

/// Optional user config at %APPDATA%\quill\config.json (with
/// ~/.config/quill/config.json honored as a fallback for parity with the
/// macOS build):
///
///     {
///       "recordings_dir": "~/Recordings",
///       "transcription": { "enabled": true, "engine": "whisper", "model": "base.en" },
///       "on_stop": "my-hook.cmd"
///     }
///
/// Resolution order for the recordings root: --out flag > config file >
/// %USERPROFILE%\Recordings. `on_stop` is a shell command spawned with the
/// session directory as its argument — after the transcript is written, or
/// right after recording when transcription is disabled.
internal static class Config
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "quill",
        "config.json");

    private static string LegacyPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "quill", "config.json");

    public static string DefaultRoot { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Recordings");

    /// The configured recordings root, or null if no config file / no key.
    public static string? RecordingsDir()
    {
        var dir = Load()?.RecordingsDir;
        return string.IsNullOrWhiteSpace(dir) ? null : ExpandPath(dir);
    }

    /// Command spawned through cmd.exe after each session's transcript is
    /// written (or after recording, if transcription is disabled), or null.
    public static string? OnStop()
    {
        var cmd = Load()?.OnStop;
        return string.IsNullOrWhiteSpace(cmd) ? null : cmd;
    }

    /// Whether finished recordings are transcribed automatically. Default on.
    public static bool TranscriptionEnabled() => Load()?.Transcription?.Enabled ?? true;

    /// Configured engine name. Only "whisper" ships on Windows; the
    /// coordinator warns and falls back for anything else.
    public static string TranscriptionEngine() => Load()?.Transcription?.Engine ?? "whisper";

    /// whisper.cpp model to run, e.g. "base.en", "small.en", "large-v3-turbo".
    public static string TranscriptionModel() => Load()?.Transcription?.Model ?? "base.en";

    /// Capture the default communications device instead of the default
    /// multimedia device for the mic track. Meeting apps often switch the
    /// communications device only, leaving multimedia pointed elsewhere.
    public static bool MicUseCommunicationsDevice() => Load()?.MicUseCommunicationsDevice ?? false;

    /// Resolve the recordings root from an optional CLI override.
    public static string ResolveRoot(string? cliOverride)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride)) return ExpandPath(cliOverride);
        return RecordingsDir() ?? DefaultRoot;
    }

    /// Expand a leading ~ and any %VARS%, then make the path absolute.
    public static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) ||
            expanded.StartsWith(@"~\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Length <= 1 ? home : System.IO.Path.Combine(home, expanded[2..]);
        }
        return System.IO.Path.GetFullPath(expanded);
    }

    /// Parse the config file, caching until it changes on disk. Every accessor
    /// above goes through here, and the daemon reads config on each recording
    /// stop — re-parsing per property would be a syscall storm for nothing,
    /// while caching forever would mean restarting quill after every edit.
    ///
    /// A malformed config is reported on stderr rather than silently ignored:
    /// recordings landing in an unexpected place is worse than a warning.
    private static ConfigFile? Load()
    {
        var path = File.Exists(Path) ? Path : File.Exists(LegacyPath) ? LegacyPath : null;
        if (path is null) return null;

        lock (CacheGate)
        {
            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                return _cached;
            }
            if (_cachedPath == path && _cachedStamp == stamp) return _cached;

            _cachedPath = path;
            _cachedStamp = stamp;
            try
            {
                using var stream = File.OpenRead(path);
                _cached = JsonSerializer.Deserialize<ConfigFile>(stream, Options);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"warning: {path} is not valid JSON — ignoring config");
                _cached = null;
            }
            return _cached;
        }
    }

    private static readonly object CacheGate = new();
    private static ConfigFile? _cached;
    private static string? _cachedPath;
    private static DateTime _cachedStamp;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class ConfigFile
    {
        [JsonPropertyName("recordings_dir")] public string? RecordingsDir { get; init; }
        [JsonPropertyName("on_stop")] public string? OnStop { get; init; }
        [JsonPropertyName("transcription")] public TranscriptionSection? Transcription { get; init; }

        [JsonPropertyName("mic_use_communications_device")]
        public bool? MicUseCommunicationsDevice { get; init; }
    }

    private sealed class TranscriptionSection
    {
        [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
        [JsonPropertyName("engine")] public string? Engine { get; init; }
        [JsonPropertyName("model")] public string? Model { get; init; }
    }
}
