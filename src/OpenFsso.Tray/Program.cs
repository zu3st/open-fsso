using System.Security.Principal;
using OpenFsso.Tray;
using Windows.Win32;
using Windows.Win32.System.Recovery;

if (args.Length != 0)
{
    return;
}

using (var identity = WindowsIdentity.GetCurrent())
{
    if (identity.IsSystem)
    {
        return; // No UI in Session 0.
    }
}

using var single = new Mutex(true, @"Local\OpenFsso.Tray", out bool created);
if (!created)
{
    return;
}

// Windows Installer's Restart Manager can restore an existing tray after an update.
PInvoke.RegisterApplicationRestart(null, REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_CRASH
    | REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_HANG
    | REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_REBOOT);

using var app = new TrayApplication();
app.Run();
