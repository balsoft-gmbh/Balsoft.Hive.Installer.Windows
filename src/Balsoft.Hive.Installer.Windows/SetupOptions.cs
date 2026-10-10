using System;
using System.Collections.Generic;
using System.Linq;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>How much of the installer the user sees.</summary>
public enum SetupUi
{
    /// <summary>The full wizard.</summary>
    Wizard,
    /// <summary>/SILENT: progress only, no questions.</summary>
    Silent,
    /// <summary>/VERYSILENT: no window at all.</summary>
    VerySilent,
}

/// <summary>
/// The installer's command line, the switches that update mechanisms and IT scripts use:
/// <c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="..."</c>, <c>/DIR=</c>, <c>/TASKS=</c>, and the
/// service account switches <c>/SvcAccount=DOMAIN\user /SvcPassword=***</c>. Windows has already removed the quotes,
/// so <c>/LOG=C:\a b\c.log</c> arrives as one argument. Switches are case-insensitive;
/// unknown ones are reported and ignored.
/// </summary>
public sealed class SetupOptions
{
    /// <summary>How much of the installer the user sees.</summary>
    public SetupUi Ui { get; private set; } = SetupUi.Wizard;
    /// <summary>/SUPPRESSMSGBOXES: no message boxes in a silent run.</summary>
    public bool SuppressMessageBoxes { get; private set; }
    /// <summary>/UNINSTALL: remove the product.</summary>
    public bool Uninstall { get; private set; }

    /// <summary>Log file path; null = the default log in %TEMP%.</summary>
    public string? LogPath { get; private set; }

    /// <summary>/LOG given without a path: log to the default file.</summary>
    public bool LogRequested { get; private set; }

    /// <summary>Install folder from /DIR; null = the existing install, else the default.</summary>
    public string? InstallDir { get; private set; }

    /// <summary>/SvcAccount: run-as account for a fresh install.</summary>
    public string? ServiceAccount { get; private set; }
    /// <summary>/SvcPassword: its password (never logged).</summary>
    public string? ServicePassword { get; private set; }

    /// <summary>Create the desktop shortcut (/DESKTOPICON or /TASKS="desktopicon").</summary>
    public bool DesktopIcon { get; private set; }

    /// <summary>/ALLOWDOWNGRADE: install even when a newer version is installed (support cases only).</summary>
    public bool AllowDowngrade { get; private set; }

    /// <summary>Internal: the uninstaller restarted itself from %TEMP%; this is the install folder it serves.</summary>
    public string? FromTempFor { get; private set; }

    /// <summary>Arguments that were not understood (reported in the log, otherwise ignored).</summary>
    public IList<string> Ignored { get; } = new List<string>();

    /// <summary>True for /SILENT and /VERYSILENT.</summary>
    public bool IsSilent => Ui != SetupUi.Wizard;

    /// <summary>Parse a command line (the arguments as Windows passes them, quotes removed).</summary>
    public static SetupOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var o = new SetupOptions();
        foreach (var raw in args)
        {
            string arg = raw.Trim();
            if (arg.Length == 0) continue;
            if (arg[0] is not ('/' or '-')) { o.Ignored.Add(arg); continue; }

            string body = arg.TrimStart('/', '-');
            int eq = body.IndexOf('=');
            string name = (eq < 0 ? body : body[..eq]).ToUpperInvariant();
            string? value = eq < 0 ? null : Unquote(body[(eq + 1)..]);

            switch (name)
            {
                case "SILENT": if (o.Ui != SetupUi.VerySilent) o.Ui = SetupUi.Silent; break;
                case "VERYSILENT": o.Ui = SetupUi.VerySilent; break;
                case "SUPPRESSMSGBOXES": o.SuppressMessageBoxes = true; break;
                case "NORESTART": case "NOCANCEL": case "NOCLOSEAPPLICATIONS": case "SP-": break;   // nothing to do: this installer never restarts
                case "LOG":
                    o.LogRequested = true;
                    o.LogPath = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "DIR": o.InstallDir = string.IsNullOrWhiteSpace(value) ? null : value; break;
                case "SVCACCOUNT": o.ServiceAccount = value?.Trim(); break;
                case "SVCPASSWORD": o.ServicePassword = value; break;
                case "DESKTOPICON": o.DesktopIcon = true; break;
                case "TASKS":
                    // A comma list; "!name" switches a task off.
                    if ((value ?? "").Split(',').Any(t => t.Trim().Equals("desktopicon", StringComparison.OrdinalIgnoreCase)))
                        o.DesktopIcon = true;
                    break;
                case "UNINSTALL": o.Uninstall = true; break;
                case "FROMTEMP": o.FromTempFor = value; o.Uninstall = true; break;
                case "ALLOWDOWNGRADE": o.AllowDowngrade = true; break;
                default: o.Ignored.Add(arg); break;
            }
        }
        return o;
    }

    /// <summary>The same switches as a command line, for restarting the uninstaller from %TEMP%.</summary>
    public IReadOnlyList<string> ToArguments()
    {
        var a = new List<string>();
        if (Ui == SetupUi.Silent) a.Add("/SILENT");
        if (Ui == SetupUi.VerySilent) a.Add("/VERYSILENT");
        if (SuppressMessageBoxes) a.Add("/SUPPRESSMSGBOXES");
        if (LogPath is not null) a.Add("/LOG=" + LogPath);
        else if (LogRequested) a.Add("/LOG");
        if (Uninstall) a.Add("/UNINSTALL");
        return a;
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        return v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;
    }
}
