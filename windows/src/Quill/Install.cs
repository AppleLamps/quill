using Microsoft.Win32;

namespace Quill;

/// Manage the launch-at-login registration.
///
/// The HKCU Run key, not a scheduled task or a service: quill records the
/// audio of the interactive session, so it must run as the logged-in user in
/// that user's session, and it must be trivially removable by that user.
internal static class Install
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "quill";

    public static int Run(bool launchAtLogin, bool uninstall)
    {
        if (launchAtLogin == uninstall)
        {
            Console.Error.WriteLine("specify exactly one of --launch-at-login or --uninstall");
            return 64;
        }
        return uninstall ? RemoveEntry() : WriteEntry();
    }

    private static int WriteEntry()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
        {
            Console.Error.WriteLine("couldn't locate the quill executable");
            return 1;
        }

        // --background so the login launch doesn't flash a console window.
        var command = $"\"{exe}\" run --background";
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);

        Console.WriteLine("+ launch-at-login installed");
        Console.WriteLine($"  key:     HKCU\\{RunKey}\\{ValueName}");
        Console.WriteLine($"  command: {command}");
        return 0;
    }

    private static int RemoveEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is null)
        {
            Console.WriteLine($"nothing to remove (no {ValueName} entry under HKCU\\{RunKey})");
            return 0;
        }
        key.DeleteValue(ValueName);
        Console.WriteLine("+ launch-at-login removed");
        return 0;
    }
}
