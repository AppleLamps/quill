using System.Diagnostics;
using Quill.Transcription;
using Quill.UI;

namespace Quill;

/// Owns the tray icon, the current recording session, and the elapsed-time
/// ticker. All state transitions happen on the UI thread; the one blocking
/// step — finalizing a stopped session — is handed to a background thread.
internal sealed class AppController : IDisposable
{
    /// Finalizing waits on both tracks draining (3s each, worst case). Give a
    /// backgrounded stop a little more than that before letting the process
    /// exit out from under it.
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly TrayController _tray = new();
    private readonly TranscriptionCoordinator _transcription = new();
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 1000 };
    private readonly SynchronizationContext? _ui;
    private RecordingSession? _session;
    private Task _finalizing = Task.CompletedTask;

    public AppController(string root)
    {
        _root = root;
        _ui = SynchronizationContext.Current;
        _tray.OnToggle = Toggle;
        _tray.OnOpenFolder = OpenFolder;
        _tray.OnQuit = Shutdown;
        _tray.Update(TrayState.Idle);
        _ticker.Tick += (_, _) => Tick();

        _transcription.StatusChanged = status => OnUi(() => ShowTranscription(status));
        _transcription.ResumePending(_root);
    }

    /// Stop any live session cleanly (finalizing files) and exit.
    public void Shutdown()
    {
        // Inline, not backgrounded: the message loop is about to end, so there
        // is no thread left to post the completion back to.
        FinishSession(synchronous: true);

        // A stop started from the menu may still be draining. Letting
        // Application.Exit() run now would tear the process down mid-write —
        // exactly what the drain exists to prevent.
        try
        {
            _finalizing.Wait(FinalizeTimeout);
        }
        catch (AggregateException)
        {
            // Already reported by FinalizeSession; nothing to add here.
        }

        Application.Exit();
    }

    private void Toggle()
    {
        // The tray item is disabled while finalizing, but a queued click can
        // still land after the menu was drawn.
        if (!_finalizing.IsCompleted) return;
        if (_session is null) StartSession();
        else FinishSession(synchronous: false);
    }

    private void StartSession()
    {
        var session = new RecordingSession(_root);
        try
        {
            session.Start();
        }
        catch (Exception e)
        {
            session.Dispose();
            Console.Error.WriteLine($"recording start failed: {e.Message}");
            Notify.User("quill — recording failed", e.Message);
            return;
        }

        _session = session;
        Console.Error.WriteLine($"● recording → {session.Dir}");
        _tray.Update(TrayState.Recording, "0:00");
        _ticker.Start();
    }

    /// Close out the live session: stop both tracks, write meta.json, and hand
    /// the folder to the transcription queue.
    ///
    /// This blocks for as long as WASAPI takes to drain, so an interactive
    /// stop does it on a background thread — holding the UI thread for a
    /// wedged device would freeze the tray for seconds with no explanation.
    private void FinishSession(bool synchronous)
    {
        if (_session is not { } session) return;
        // Clear the state first: if finalizing throws, the tray must not stay
        // stuck red on a session that is no longer capturing.
        _session = null;
        _ticker.Stop();
        _tray.Update(TrayState.Finalizing);

        if (synchronous)
        {
            FinalizeSession(session);
            SessionFinalized();
            return;
        }

        _finalizing = Task.Run(() =>
        {
            try
            {
                FinalizeSession(session);
            }
            finally
            {
                OnUi(SessionFinalized);
            }
        });
    }

    private void FinalizeSession(RecordingSession session)
    {
        try
        {
            session.Stop();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"recording stop failed: {e.Message}");
            Notify.User("quill — recording stop failed", e.Message);
        }
        finally
        {
            session.Dispose();
        }

        Console.Error.WriteLine(
            $"○ stopped · {Format(DateTime.UtcNow - session.StartedAt)} · {session.Dir}");

        _transcription.Enqueue(session.Dir);
    }

    private void SessionFinalized() => _tray.Update(TrayState.Idle);

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
        _tray.Update(TrayState.Recording, Format(DateTime.UtcNow - session.StartedAt));
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_root);
        Process.Start(new ProcessStartInfo(_root) { UseShellExecute = true });
    }

    /// Run `action` on the UI thread, or inline when there is no UI context
    /// (the `quill transcribe` path has no message loop).
    private void OnUi(Action action)
    {
        if (_ui is { } ui) ui.Post(_ => action(), null);
        else action();
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
