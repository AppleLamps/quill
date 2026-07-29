using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Transcription;

/// Post-recording pipeline: a serial queue of session folders to transcribe.
/// mic.wav → "me", system.wav → "them"; each track's segments are shifted by
/// its start offset, merged by timestamp, and written as transcript.json
/// (canonical) plus transcript.md (readable). The filesystem is the queue —
/// ResumePending() rescans at launch, so a crash or quit mid-transcription
/// just retries on next run. Failures append to the session's transcribe.log
/// and never block later jobs.
internal sealed class TranscriptionCoordinator
{
    public abstract record Status
    {
        public sealed record Idle : Status;
        public sealed record Transcribing(string Session, int Queued) : Status;
        public sealed record Failed(string Session) : Status;
    }

    private readonly Queue<string> _queue = new();
    private readonly object _gate = new();
    private ITranscriptionEngine? _engine;
    private string? _lastFailure;
    private bool _draining;

    public Action<Status>? StatusChanged { get; set; }

    /// Queue a finished session. With transcription disabled in config, the
    /// on_stop hook still fires — it just gets an untranscribed folder.
    public void Enqueue(string sessionDir)
    {
        if (!Config.TranscriptionEnabled())
        {
            RunHook(sessionDir);
            return;
        }
        lock (_gate)
        {
            if (!_queue.Contains(sessionDir)) _queue.Enqueue(sessionDir);
        }
        DrainIfIdle();
    }

    /// Scan the recordings root for sessions that finished (meta.json exists)
    /// but were never transcribed. Folder names sort chronologically, so
    /// oldest-first is a name sort.
    public void ResumePending(string root)
    {
        if (!Config.TranscriptionEnabled() || !Directory.Exists(root)) return;

        var pending = Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "meta.json"))
                        && !File.Exists(Path.Combine(d, "transcript.json")))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        lock (_gate)
        {
            foreach (var dir in pending.Where(dir => !_queue.Contains(dir))) _queue.Enqueue(dir);
        }
        if (pending.Count > 0)
            Console.Error.WriteLine($"resuming {pending.Count} untranscribed session(s)");
        DrainIfIdle();
    }

    /// Transcribe one session folder inline, overwriting any transcript it
    /// already has. This is what `quill transcribe` runs — re-transcribing an
    /// old session, or finishing one whose job failed, without a tray.
    public async Task RunOnceAsync(string dir, CancellationToken cancellationToken = default)
    {
        try
        {
            await TranscribeAsync(dir).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _engine?.Dispose();
            _engine = null;
        }
    }

    private void DrainIfIdle()
    {
        lock (_gate)
        {
            if (_draining || _queue.Count == 0) return;
            _draining = true;
            _lastFailure = null;
        }
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            string dir;
            int queued;
            lock (_gate)
            {
                if (_queue.Count == 0) break;
                dir = _queue.Dequeue();
                queued = _queue.Count;
            }

            Publish(new Status.Transcribing(Path.GetFileName(dir), queued));
            try
            {
                await TranscribeAsync(dir).ConfigureAwait(false);
                Notify.User("quill — transcript ready", Path.GetFileName(dir));
                RunHook(dir);
            }
            catch (Exception e)
            {
                Log(dir, $"transcription failed: {e.Message}");
                _lastFailure = Path.GetFileName(dir);
                Notify.User("quill — transcription failed",
                    $"{Path.GetFileName(dir)} — see transcribe.log");
            }
        }

        _engine?.Dispose();
        _engine = null;
        Publish(_lastFailure is null ? new Status.Idle() : new Status.Failed(_lastFailure));
        lock (_gate) { _draining = false; }
        // An enqueue that landed between the loop exiting and the engine
        // release finishing would otherwise sit until the next enqueue.
        DrainIfIdle();
    }

    private async Task TranscribeAsync(string dir)
    {
        var meta = SessionMeta.Read(dir);
        var engine = await PreparedEngineAsync().ConfigureAwait(false);

        var merged = new List<TranscriptSegmentRecord>();
        foreach (var track in meta.Tracks)
        {
            var audio = Path.Combine(dir, track.File);
            if (!File.Exists(audio))
            {
                Log(dir, $"skipping missing track {track.File}");
                continue;
            }
            Log(dir, $"transcribing {track.File} ({engine.Name} {engine.Model})");

            // One bad track (empty, truncated) shouldn't cost us the other's
            // transcript — log it and keep going.
            IReadOnlyList<TranscriptSegment> segments;
            try
            {
                segments = await engine.TranscribeAsync(audio).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(dir, $"skipping {track.File}: {e.Message}");
                continue;
            }

            merged.AddRange(segments.Select(s => new TranscriptSegmentRecord(
                track.Speaker,
                (int)s.Start.TotalMilliseconds + track.OffsetMs,
                (int)s.End.TotalMilliseconds + track.OffsetMs,
                s.Text)));
        }
        merged.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        var transcript = new TranscriptFile(
            engine.Name,
            engine.Model,
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            merged);
        transcript.Write(dir);
        Log(dir, $"done — {merged.Count} segments");
    }

    private async Task<ITranscriptionEngine> PreparedEngineAsync()
    {
        if (_engine is not null) return _engine;
        var configured = Config.TranscriptionEngine();
        if (configured != "whisper")
            Console.Error.WriteLine(
                $"warning: unknown transcription engine \"{configured}\" — using whisper");

        var engine = new WhisperEngine(Config.TranscriptionModel());
        await engine.PrepareAsync().ConfigureAwait(false);
        _engine = engine;
        return engine;
    }

    /// Fires the configured on_stop command with the session directory as its
    /// sole argument, after the transcript exists (or immediately after
    /// recording when transcription is disabled).
    private static void RunHook(string dir)
    {
        var cmd = Config.OnStop();
        if (cmd is null) return;
        try
        {
            // /s /c "..." is the one cmd.exe form that survives a quoted
            // program path inside the command string; plain /c strips the
            // outer quotes and breaks on `"C:\My Tools\hook.cmd"`.
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/s /c \"{cmd} \"{dir}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            Log(dir, $"on_stop hook failed to launch: {e.Message}");
        }
    }

    private static void Log(string dir, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {message}{Environment.NewLine}";
        try
        {
            File.AppendAllText(Path.Combine(dir, "transcribe.log"), line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A log we can't write is not worth failing a transcript over.
        }
        Console.Error.Write(line);
    }

    private void Publish(Status status) => StatusChanged?.Invoke(status);

    /// The slice of meta.json the coordinator needs: which files exist, who
    /// they represent, and how far each track started after the earliest one.
    private sealed record SessionMeta(IReadOnlyList<SessionMeta.Track> Tracks)
    {
        public sealed record Track(string File, string Speaker, int OffsetMs);

        public static SessionMeta Read(string dir)
        {
            var path = Path.Combine(dir, "meta.json");
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("files", out var files))
                throw new InvalidDataException($"can't parse {path}");

            // Sessions recorded before offsets were captured default to 0 —
            // tracks start within tens of milliseconds of each other anyway.
            doc.RootElement.TryGetProperty("start_offset_ms", out var offsets);
            var tracks = new List<Track>();
            foreach (var (key, speaker) in new[] { ("mic", "me"), ("system", "them") })
            {
                // Tracks always live in the session folder; take the file name
                // only, so a hand-edited meta.json can't point somewhere else.
                if (!files.TryGetProperty(key, out var file)
                    || Path.GetFileName(file.GetString()) is not { Length: > 0 } name) continue;
                var offset = offsets.ValueKind == JsonValueKind.Object
                             && offsets.TryGetProperty(key, out var o)
                             && o.ValueKind == JsonValueKind.Number
                             && o.TryGetInt32(out var ms) ? ms : 0;
                tracks.Add(new Track(name, speaker, offset));
            }
            return new SessionMeta(tracks);
        }
    }

    /// Canonical transcript. Property names are the JSON schema — this record
    /// exists to be serialized.
    private sealed record TranscriptFile(
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("created_at")] string CreatedAt,
        [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegmentRecord> Segments)
    {
        /// Write transcript.json and render transcript.md. Both writes go to a
        /// temp file first and are renamed into place, so a partially written
        /// transcript never exists on disk — ResumePending treats presence of
        /// transcript.json as "done".
        public void Write(string dir)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomic(Path.Combine(dir, "transcript.json"), json);
            WriteAtomic(Path.Combine(dir, "transcript.md"), Render(Path.GetFileName(dir)));
        }

        private static void WriteAtomic(string path, string contents)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }

        private string Render(string title)
        {
            var sb = new StringBuilder()
                .Append("# ").Append(title).Append("\n\n")
                .Append("engine: ").Append(Engine).Append(" (").Append(Model).Append(")\n\n");
            foreach (var s in Segments)
                sb.Append("**[").Append(Clock(s.StartMs)).Append("] ").Append(s.Speaker)
                  .Append(":** ").Append(s.Text).Append("\n\n");
            return sb.ToString();
        }

        private static string Clock(int ms)
        {
            var total = ms / 1000;
            int h = total / 3600, m = total % 3600 / 60, s = total % 60;
            return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
        }
    }

    private sealed record TranscriptSegmentRecord(
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("start_ms")] int StartMs,
        [property: JsonPropertyName("end_ms")] int EndMs,
        [property: JsonPropertyName("text")] string Text);
}
