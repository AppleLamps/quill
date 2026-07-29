using System.Diagnostics;
using Quill.Transcription;
using Quill.UI;

namespace Quill;

/// Owns the tray icon, the current recording session, and the elapsed-time
/// ticker. All state transitions happen on the UI thread.
internal sealed class AppController : IDisposable
{
    private readonly string _root;
    private readonly TrayController _tray = new();
    private readonly TranscriptionCoordinator _transcription = new();
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 1000 };
    private RecordingSession? _session;

    public AppController(string root)
    {
        _root = root;
        _tray.OnToggle = Toggle;
        _tray.OnOpenFolder = OpenFolder;
        _tray.OnQuit = Shutdown;
        _tray.Update(recording: false, elapsed: null);
        _ticker.Tick += (_, _) => Tick();

        var ui = SynchronizationContext.Current;
        _transcription.StatusChanged = status =>
        {
            if (ui is null) ShowTranscription(status);
            else ui.Post(_ => ShowTranscription(status), null);
        };
        _transcription.ResumePending(_root);
    }

    /// Stop any live session cleanly (finalizing files) and exit.
    public void Shutdown()
    {
        StopSession();
        Application.Exit();
    }

    private void Toggle()
    {
        if (_session is null) StartSession();
        else StopSession();
    }

    private void StartSession()
    {
        try
        {
            var session = new RecordingSession(_root);
            session.Start();
            _session = session;
            Console.Error.WriteLine($"● recording → {session.Dir}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"recording start failed: {e.Message}");
            Notify.User("quill — recording failed", e.Message);
            return;
        }

        _tray.Update(recording: true, elapsed: "0:00");
        _ticker.Start();
    }

    private void StopSession()
    {
        if (_session is not { } session) return;
        session.Stop();
        _session = null;
        _ticker.Stop();
        _tray.Update(recording: false, elapsed: null);
        Console.Error.WriteLine(
            $"○ stopped · {Format(DateTime.UtcNow - session.StartedAt)} · {session.Dir}");

        _transcription.Enqueue(session.Dir);
    }

    private void ShowTranscription(TranscriptionCoordinator.Status status) =>
        _tray.UpdateTranscription(status switch
        {
            TranscriptionCoordinator.Status.Transcribing t =>
                t.Queued > 0
                    ? $"transcribing {t.Session} · {t.Queued} queued"
                    : $"transcribing {t.Session}",
            TranscriptionCoordinator.Status.Failed f => $"transcription failed · {f.Session}",
            _ => null,
        });

    private void Tick()
    {
        if (_session is not { } session) return;
        _tray.Update(recording: true, elapsed: Format(DateTime.UtcNow - session.StartedAt));
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_root);
        Process.Start(new ProcessStartInfo(_root) { UseShellExecute = true });
    }

    private static string Format(TimeSpan elapsed)
    {
        var total = (int)elapsed.TotalSeconds;
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    public void Dispose()
    {
        _ticker.Dispose();
        _tray.Dispose();
    }
}
