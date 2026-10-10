namespace Balsoft.Hive.Installer.Windows;

/// <summary>
/// The process exit codes of Setup, for scripts and applications that start an install or update
/// and check how it ended.
/// </summary>
public enum InstallerExitCode
{
    /// <summary>Installed, upgraded or removed.</summary>
    Success = 0,

    /// <summary>Setup could not start (bad payload, invalid product definition, already running).</summary>
    InitFailed = 1,

    /// <summary>Cancelled before anything was changed.</summary>
    Cancelled = 2,

    /// <summary>A step failed while files were being installed; an upgrade was rolled back.</summary>
    InstallFailed = 4,

    /// <summary>Preparing failed (service account, a running program, the service would not stop); nothing was changed.</summary>
    PrepareFailed = 7,
}
