using System;
using System.IO;
using Microsoft.Win32;

namespace LanLink;

/// <summary>
/// Owns LanLink's "start with Windows" registry state.
///
/// Design rule: <b>the per-user (HKCU) Run entry is the single source of truth.</b>
/// Windows launches an app once per Run entry, and each of LanLink's entries
/// carries its own start-mode flag, so two entries mean two launches whose
/// race decides how the app appears — a stale machine-wide entry can silently
/// override the mode picked in Settings, and switching autostart off appears to
/// do nothing.  Only one entry may exist, and it must be one an unelevated app
/// can rewrite, which rules out HKLM.
///
/// The installer therefore never writes a Run entry itself.  Its autostart
/// checkbox writes a *request* under <see cref="RequestKey"/> (HKLM is the only
/// hive a per-machine MSI can write reliably — its execute sequence runs as
/// SYSTEM, so an HKCU write there would land in SYSTEM's hive, not the user's).
/// The app consumes that request on first run after the install and turns it
/// into the real HKCU entry.  See <see cref="ConsumeInstallerRequest"/>.
/// </summary>
public static class Autostart
{
    private const string RunKey     = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RequestKey = @"SOFTWARE\LanLink";
    private const string RequestValueName = "AutostartRequest";
    private const string AppName    = "LanLink";

    /// <summary>True if autostart is configured per-user (HKCU) or machine-wide (HKLM).</summary>
    public static bool IsEnabled()
        => ReadRunValue(Registry.CurrentUser)  is not null
        || ReadRunValue(Registry.LocalMachine) is not null;

    /// <summary>
    /// Point the per-user (HKCU) Run entry at this exe with the flag matching the
    /// chosen start mode, then drop any machine-wide (HKLM) entry left behind by
    /// an older installer.
    ///
    /// Returns false if a machine-wide entry survived (removing it needs admin).
    /// </summary>
    public static bool Apply(bool enable, StartupMode mode)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is not null)
            {
                if (enable) key.SetValue(AppName, CommandLineFor(mode));
                else        key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch { /* non-critical — the user can set it manually */ }

        // Nothing machine-wide to reconcile.
        if (ReadRunValue(Registry.LocalMachine) is null) return true;

        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
            hklm?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { /* not elevated — reported to the caller below */ }

        return ReadRunValue(Registry.LocalMachine) is null;
    }

    /// <summary>
    /// Apply the autostart choice made in the installer, exactly once per install.
    ///
    /// The MSI stamps <c>HKLM\SOFTWARE\LanLink\AutostartRequest</c> with
    /// <c>"&lt;version&gt;|&lt;1 if the box was ticked&gt;"</c>.  Comparing that
    /// stamp against <see cref="AppSettings.AutostartRequestHandled"/> means each
    /// install's explicit choice is honoured once and then left alone: turning
    /// autostart off in Settings afterwards sticks, because the stamp hasn't
    /// changed, while the next install's answer is applied because it has.
    ///
    /// Mutates and saves <paramref name="settings"/> when there is something to
    /// apply.  Returns true if the settings changed.
    /// </summary>
    public static bool ConsumeInstallerRequest(AppSettings settings)
    {
        string request = ReadRequest() ?? "";
        if (request == (settings.AutostartRequestHandled ?? "")) return false;

        // No stamp at all means an install too old to record an answer (or a
        // copy that was never installed) — nothing to apply, but remember that
        // so we don't re-check every launch.
        int bar = request.IndexOf('|');
        if (bar >= 0)
            settings.RunOnStartup = request[(bar + 1)..].Trim() == "1";

        // The setup checkbox promises "minimized to the tray".  Honour that
        // literally only on a fresh install — if settings.json already existed
        // the user has a start-mode preference that setup must not clobber.
        if (settings.RunOnStartup && !settings.ExistedOnDisk)
            settings.StartupMode = StartupMode.Tray;

        Apply(settings.RunOnStartup, settings.StartupMode);
        settings.AutostartRequestHandled = request;
        settings.Save();
        return true;
    }

    /// <summary>The Run-entry command line for a start mode (exe path + flag).</summary>
    private static string CommandLineFor(StartupMode mode)
    {
        string exePath = Environment.ProcessPath
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LanLink.exe");
        string flag = mode switch
        {
            StartupMode.Tray      => " --minimized",
            StartupMode.Minimized => " --minimized-taskbar",
            _                     => ""
        };
        return $"\"{exePath}\"{flag}";
    }

    private static string? ReadRequest()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RequestKey);
            return key?.GetValue(RequestValueName) as string;
        }
        catch { return null; }
    }

    private static string? ReadRunValue(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(RunKey);
            return key?.GetValue(AppName) as string;
        }
        catch { return null; }
    }
}
