using System.Runtime.InteropServices;

namespace Quill;

/// quill's command line. Hand-rolled rather than pulled from a package: three
/// subcommands and four flags don't justify a dependency, and the parse has to
/// stay predictable for the Run-key command line that install writes.
internal static class Program
{
    private const string Usage = """
        quill — local meeting recorder + transcriber. Records mic and system
        audio as two tracks, then transcribes on-device.

        usage:
          quill [run] [--out <dir>] [--background]   run the tray daemon (default)
          quill doctor                               check devices, folder, model
          quill install --launch-at-login            start quill at login
          quill install --uninstall                  remove the login entry
          quill transcribe <session-dir>             (re)transcribe one session

        options:
          --out <dir>    recordings root (overrides the config file)
          --background   hide the console window (used by launch-at-login)
          -h, --help     this text
        """;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    private const int SwHide = 0;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "run";

        if (args.Contains("-h") || args.Contains("--help"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        switch (command)
        {
            case "run":
                return RunDaemon(args);
            case "doctor":
            {
                var checks = DoctorReport.Run(Config.ResolveRoot(OptionValue(args, "--out")));
                DoctorReport.Print(checks);
                return DoctorReport.AllOk(checks) ? 0 : 1;
            }
            case "transcribe":
                return Transcribe(args);
            case "install":
                return Install.Run(
                    launchAtLogin: args.Contains("--launch-at-login"),
                    uninstall: args.Contains("--uninstall"));
            default:
                Console.Error.WriteLine($"unknown command \"{command}\"\n");
                Console.Error.WriteLine(Usage);
                return 64;
        }
    }

    private static int Transcribe(string[] args)
    {
        var dir = args.SkipWhile(a => a != "transcribe").Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (dir is null)
        {
            Console.Error.WriteLine("usage: quill transcribe <session-dir>");
            return 64;
        }
        dir = Config.ExpandPath(dir);
        if (!File.Exists(Path.Combine(dir, "meta.json")))
        {
            Console.Error.WriteLine($"{dir} is not a quill session (no meta.json)");
            return 1;
        }

        try
        {
            new Transcription.TranscriptionCoordinator().RunOnceAsync(dir).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"transcription failed: {e.Message}");
            return 1;
        }
    }

    private static int RunDaemon(string[] args)
    {
        var root = Config.ResolveRoot(OptionValue(args, "--out"));

        // Non-blocking: warnings at startup are informational, only hard
        // failures (no devices, unwritable folder) stop the daemon.
        var checks = DoctorReport.Run(root);
        if (!DoctorReport.AllOk(checks))
        {
            Console.Error.WriteLine("startup checks failed:");
            DoctorReport.Print(checks);
            return 1;
        }

        if (args.Contains("--background")) ShowWindow(GetConsoleWindow(), SwHide);

        ApplicationConfiguration.Initialize();
        // Install the WinForms context before anything captures it: the tray
        // and the transcription queue both marshal back onto this thread, and
        // WinForms only installs it lazily on first control creation.
        var context = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);

        using var controller = new AppController(root);

        // Ctrl+C in the console must finalize the WAVs, not tear the process
        // down mid-write — hand the shutdown to the UI thread and let the
        // message loop exit on its own.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("\nshutting down");
            context.Post(_ => controller.Shutdown(), null);
        };

        Console.Error.WriteLine($"quill up · recordings → {root} · ^C to quit");
        Application.Run();
        return 0;
    }

    /// Value of `--name <value>`, or null when absent.
    private static string? OptionValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) return null;
        var value = args[index + 1];
        return value.StartsWith('-') ? null : value;
    }
}
