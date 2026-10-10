using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>What is on the machine before Setup changes anything.</summary>
internal sealed record ExistingInstall(string? Dir, string? Version, bool ServiceExists, bool DesktopShortcut)
{
    public bool IsUpgrade => Dir is not null;
}

/// <summary>The choices for one install run (from the wizard or the command line).</summary>
internal sealed record InstallChoices(string InstallDir, string? ServiceAccount, string? ServicePassword, bool DesktopShortcut, bool AllowDowngrade = false);

/// <summary>
/// Installs or upgrades a product: check the service account, wait for the product's programs to
/// close, drop stale reboot renames, stop the service, copy the files, register the service,
/// shortcuts and the uninstall entry. Files are backed up before they are replaced and restored
/// when a later step fails, so a failed upgrade leaves the previous version running.
/// </summary>
internal sealed class InstallEngine
{
    private const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string PendingRenamesValue = "PendingFileRenameOperations";
    private const string BackupFolder = ".setup-backup";

    private readonly ProductDefinition _product;
    private readonly Payload _payload;
    private readonly SetupLog _log;
    private readonly bool _silent;

    /// <summary>Asks the user a yes/no question; in silent mode the engine never calls it.</summary>
    public Func<string, bool> AskYesNo { get; init; } = _ => false;

    /// <summary>The wizard already asked about an account Windows could not verify; do not ask again.</summary>
    public bool CredentialsAlreadyConfirmed { get; init; }

    /// <summary>Progress 0..100 and a status line.</summary>
    public Action<int, string> Progress { get; init; } = (_, _) => { };

    public InstallEngine(ProductDefinition product, Payload payload, SetupLog log, bool silent)
    {
        _product = product;
        _payload = payload;
        _log = log;
        _silent = silent;
    }

    // ── detection ────────────────────────────────────────────────────────────

    public static ExistingInstall Detect(ProductDefinition product)
    {
        string? dir = null, version = null;
        using (var hklm = NativeMethods.LocalMachine64())
        {
            using (var own = hklm.OpenSubKey($@"{InstallPlan.UninstallRoot}\{InstallPlan.UninstallKeyName(product)}"))
            {
                if (own is not null)
                {
                    dir = (own.GetValue("InstallLocation") as string)?.TrimEnd('\\');
                    version = own.GetValue("DisplayVersion") as string;
                }
            }
        }
        if (dir is not null && !Directory.Exists(dir)) dir = null;

        bool serviceExists = product.Service is not null && ServiceExists(product.Service.Name);
        bool desktop = product.Shortcuts.Any(s => s.OfferDesktop && File.Exists(DesktopShortcutPath(s)));
        return new ExistingInstall(dir, version, serviceExists, desktop);
    }

    private static bool ServiceExists(string name)
    {
        var services = ServiceController.GetServices();
        try { return services.Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        finally { foreach (var s in services) s.Dispose(); }
    }

    public static string StartMenuShortcutPath(ShortcutDefinition s) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), s.Name + ".lnk");

    public static string DesktopShortcutPath(ShortcutDefinition s) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), s.Name + ".lnk");

    // ── run ──────────────────────────────────────────────────────────────────

    /// <summary>Install or upgrade. Returns the outcome and, on failure, the reason for the user.</summary>
    public (InstallerExitCode Result, string Message) Run(ExistingInstall existing, InstallChoices choices)
    {
        string dir = choices.InstallDir.TrimEnd('\\');
        string accountText = _product.Service is null ? "no service"
            : existing.ServiceExists ? "kept"
            : string.IsNullOrWhiteSpace(choices.ServiceAccount) ? "LocalSystem" : choices.ServiceAccount!;
        _log.Info($"{(existing.IsUpgrade ? $"Upgrade from {existing.Version ?? "unknown version"}" : "Fresh install")} " +
                  $"of {_product.ProductName} {_payload.Version} in {dir}. Service account: {accountText}.");

        // 0. never replace a newer installed version by accident
        var change = InstallPlan.Compare(existing.IsUpgrade ? existing.Version : null, _payload.Version);
        _log.Info($"Installed version: {existing.Version ?? "none"}; Setup version: {_payload.Version}; {change}.");
        if (change == VersionChange.Downgrade)
        {
            if (!choices.AllowDowngrade)
            {
                string msg = DowngradeMessage(_product, existing.Version!, _payload.Version);
                _log.Error(msg);
                return (InstallerExitCode.PrepareFailed, msg);
            }
            _log.Warn($"Downgrade from {existing.Version} to {_payload.Version} allowed by /ALLOWDOWNGRADE.");
        }
        if (change == VersionChange.Unknown)
            _log.Warn($"The installed version '{existing.Version}' cannot be read; Setup continues as an update.");

        // 1. prepare: nothing is changed yet, so a failure here leaves the machine as it was
        string? problem = Prepare(existing, choices, dir);
        if (problem is not null)
        {
            _log.Error("Preparing failed: " + problem);
            return (problem.StartsWith("Cancelled", StringComparison.Ordinal) ? InstallerExitCode.Cancelled : InstallerExitCode.PrepareFailed, problem);
        }

        // 2. install, with a backup of every file that is about to be replaced
        string backupDir = Path.Combine(dir, BackupFolder);
        var backedUp = new List<string>();
        try
        {
            Progress(30, "Backing up the current files...");
            BackUp(dir, backupDir, backedUp);

            Progress(40, "Copying the program files...");
            int count = 0;
            _payload.Extract(dir,
                name => InstallPlan.ShouldWrite(_product, name, File.Exists(Path.Combine(dir, name))),
                (name, bytes) =>
                {
                    count++;
                    _log.Info(bytes < 0 ? $"  kept {name} (existing file)" : $"  wrote {name} ({bytes:N0} bytes)");
                    Progress(40 + Math.Min(30, count * 30 / Math.Max(1, _payload.FileNames.Count)), "Copying " + name + "...");
                });

            Progress(72, "Installing the uninstaller...");
            InstallUninstaller(dir);

            if (_product.Service is not null)
            {
                Progress(78, $"Registering the {_product.Service.Name} service...");
                RegisterService(dir, existing.ServiceExists, choices);
            }

            Progress(90, "Creating shortcuts...");
            CreateShortcuts(dir, choices.DesktopShortcut);

            Progress(95, "Registering the uninstall entry...");
            WriteUninstallEntry(dir);

            TryDeleteDir(backupDir);
            Progress(100, $"{_product.ProductName} {_payload.Version} is installed.");
            _log.Info("Setup finished successfully.");
            return (InstallerExitCode.Success, "");
        }
        catch (Exception ex)
        {
            _log.Error("Install failed: " + ex.Message);
            Restore(dir, backupDir, backedUp, existing.ServiceExists);
            return (InstallerExitCode.InstallFailed, ex.Message);
        }
    }

    /// <summary>What the user is told when Setup would replace a newer installed version.</summary>
    public static string DowngradeMessage(ProductDefinition product, string installed, string setup)
        => $"{product.ProductName} {installed} is installed, which is newer than this Setup ({setup}). " +
           "Setup does not replace a newer version. Download the current Setup, or run this one with /ALLOWDOWNGRADE " +
           "if the older version is really wanted.";

    // ── steps ────────────────────────────────────────────────────────────────

    private string? Prepare(ExistingInstall existing, InstallChoices choices, string dir)
    {
        if (_product.Service is not null && !existing.ServiceExists && !string.IsNullOrWhiteSpace(choices.ServiceAccount))
        {
            Progress(5, "Checking the service account...");
            var check = NativeMethods.CheckServiceCredentials(choices.ServiceAccount!.Trim(), choices.ServicePassword ?? "", out string msg);
            if (check == CredentialCheck.Bad)
                return $"The service account {choices.ServiceAccount.Trim()} could not sign in. {msg}";
            if (check == CredentialCheck.Unknown)
            {
                _log.Warn(msg);
                if (!_silent && !CredentialsAlreadyConfirmed && !AskYesNo(msg + "\n\nContinue with these credentials anyway?"))
                    return "Cancelled: the service account could not be verified.";
            }
        }

        // An application that starts its own update launches Setup and then exits; a .NET desktop
        // process needs a moment for that, and its exe cannot be replaced while it runs.
        foreach (var exe in _product.CloseBeforeInstall)
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            Progress(10, $"Waiting for {exe} to close...");
            if (WaitForExit(name, 30)) continue;
            if (_silent) return $"{exe} is still running. Close it and run Setup again.";
            if (!AskYesNo($"{exe} is still running. Close it now and continue?"))
                return $"Cancelled: {exe} is still running.";
            CloseAll(name);
            if (!WaitForExit(name, 10)) return $"{exe} could not be closed. End it in Task Manager and run Setup again.";
        }

        Progress(15, "Checking for unfinished updates...");
        ClearPendingRenames(dir);

        if (_product.Service is not null && existing.ServiceExists)
        {
            Progress(20, $"Stopping the {_product.Service.Name} service...");
            string? stop = StopService(_product.Service);
            if (stop is not null) return stop;
        }
        return null;
    }

    /// <summary>Drop reboot renames an earlier, interrupted update staged for the install folder.</summary>
    private void ClearPendingRenames(string dir)
    {
        try
        {
            byte[]? raw = NativeMethods.ReadMultiStringRaw(SessionManagerKey, PendingRenamesValue);
            if (raw is null) return;
            var (keep, drop) = PendingRenames.Split(PendingRenames.Decode(raw), dir);
            if (drop.Count == 0) return;
            foreach (var p in drop)
            {
                _log.Warn($"Dropping a reboot rename staged by an earlier update: {p.Source} -> {(p.Target.Length == 0 ? "(delete)" : p.Target)}");
                string staged = PendingRenames.StripPrefix(p.Source);
                if (staged.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(staged))
                {
                    try { File.Delete(staged); }
                    catch (Exception ex) { _log.Warn($"  could not delete {staged}: {ex.Message}"); }
                }
            }
            NativeMethods.WriteMultiStringRaw(SessionManagerKey, PendingRenamesValue,
                keep.Count == 0 ? null : PendingRenames.Encode(PendingRenames.FromPairs(keep)));
        }
        catch (Exception ex)
        {
            // Not fatal: the files are replaced directly below; this only protects against an old
            // swap that would be applied at the next reboot.
            _log.Warn("Could not check the pending reboot renames: " + ex.Message);
        }
    }

    private string? StopService(ServiceDefinition service)
    {
        try
        {
            using var sc = new ServiceController(service.Name);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                _log.Info($"Stopping service {service.Name} (status {sc.Status}).");
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return $"The {service.Name} service could not be stopped. Stop it manually (services.msc) and run Setup again.";
        }
        catch (Exception ex)
        {
            return $"The {service.Name} service could not be stopped: {ex.Message}";
        }
        // STOPPED is reported a moment before the host process is gone; the exe is only replaceable
        // once the process has exited.
        if (!WaitForExit(Path.GetFileNameWithoutExtension(service.Executable), 15))
            return $"The {service.Name} service process did not exit. End it in Task Manager and run Setup again.";
        _log.Info("Service stopped.");
        return null;
    }

    private void BackUp(string dir, string backupDir, List<string> backedUp)
    {
        TryDeleteDir(backupDir);
        foreach (var name in _payload.FileNames)
        {
            string src = Path.Combine(dir, name);
            if (!File.Exists(src) || !InstallPlan.ShouldWrite(_product, name, true)) continue;
            Directory.CreateDirectory(backupDir);
            File.Copy(src, Path.Combine(backupDir, name), overwrite: true);
            backedUp.Add(name);
        }
        if (backedUp.Count > 0) _log.Info($"Backed up {backedUp.Count} file(s) to {backupDir}.");
    }

    private void Restore(string dir, string backupDir, List<string> backedUp, bool serviceExisted)
    {
        if (backedUp.Count == 0) { TryDeleteDir(backupDir); return; }
        _log.Warn("Restoring the previous version...");
        foreach (var name in backedUp)
        {
            try { File.Copy(Path.Combine(backupDir, name), Path.Combine(dir, name), overwrite: true); }
            catch (Exception ex) { _log.Error($"  could not restore {name}: {ex.Message}"); }
        }
        if (serviceExisted && _product.Service is not null)
        {
            try
            {
                using var sc = new ServiceController(_product.Service.Name);
                if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                _log.Info("Previous version restored and the service started again.");
            }
            catch (Exception ex) { _log.Error("The service could not be started again: " + ex.Message); }
        }
        TryDeleteDir(backupDir);
    }

    /// <summary>A copy of Setup is the uninstaller ("Apps &amp; features" runs it with /UNINSTALL).</summary>
    private void InstallUninstaller(string dir)
    {
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("Setup cannot locate its own file.");
        string target = InstallPlan.UninstallerPath(_product, dir);
        if (Path.GetFullPath(self).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(self, target, overwrite: true);
        _log.Info("Uninstaller: " + target);
    }

    private void RegisterService(string dir, bool serviceExists, InstallChoices choices)
    {
        var service = _product.Service!;
        var args = service.InstallArguments(serviceExists, serviceExists ? null : choices.ServiceAccount, serviceExists ? null : choices.ServicePassword);
        int code = RunTool(dir, service.Executable, args, TimeSpan.FromMinutes(3), _log, choices.ServicePassword);
        if (code != 0)
            throw new InvalidOperationException($"Registering the {service.Name} service failed (exit code {code}); details are in the Setup log.");
    }

    /// <summary>Run a payload program, log its output (the password masked), return its exit code.</summary>
    internal static int RunTool(string dir, string exe, IReadOnlyList<string> args, TimeSpan timeout, SetupLog log, string? secret)
    {
        log.Info("Running: " + exe + " " + string.Join(' ', args.Select(a => !string.IsNullOrEmpty(secret) && a == secret ? "***" : a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)));
        var psi = new ProcessStartInfo(Path.Combine(dir, exe))
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} could not be started.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(); } catch (InvalidOperationException) { /* exited meanwhile */ }
            throw new InvalidOperationException($"{exe} did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        p.WaitForExit();
        string output = stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult();
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0))
            log.Info("  " + (!string.IsNullOrEmpty(secret) ? line.Replace(secret, "***", StringComparison.Ordinal) : line));
        return p.ExitCode;
    }

    private void CreateShortcuts(string dir, bool desktop)
    {
        foreach (var s in _product.Shortcuts)
        {
            string target = Path.Combine(dir, s.Target);
            NativeMethods.CreateShortcut(StartMenuShortcutPath(s), target, s.Description);
            _log.Info("Start menu shortcut: " + StartMenuShortcutPath(s));
            if (desktop && s.OfferDesktop)
            {
                NativeMethods.CreateShortcut(DesktopShortcutPath(s), target, s.Description);
                _log.Info("Desktop shortcut: " + DesktopShortcutPath(s));
            }
        }
    }

    private void WriteUninstallEntry(string dir)
    {
        long bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(BackupFolder, StringComparison.OrdinalIgnoreCase))
            .Sum(f => new FileInfo(f).Length);
        using var hklm = NativeMethods.LocalMachine64();
        using var key = hklm.CreateSubKey($@"{InstallPlan.UninstallRoot}\{InstallPlan.UninstallKeyName(_product)}", writable: true);
        foreach (var (name, value) in InstallPlan.UninstallEntry(_product, _payload.Version, dir, bytes / 1024, DateTime.Today))
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        _log.Info($"Uninstall entry written (HKLM\\{InstallPlan.UninstallRoot}\\{InstallPlan.UninstallKeyName(_product)}).");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Wait until no process of that name is left. Returns false when one is still running.</summary>
    public bool WaitForExit(string processName, int maxSeconds)
    {
        for (int waited = 0; ; waited++)
        {
            var running = Process.GetProcessesByName(processName);
            foreach (var p in running) p.Dispose();
            if (running.Length == 0)
            {
                if (waited > 0) _log.Info($"{processName}: exited after {waited}s.");
                return true;
            }
            if (waited >= maxSeconds)
            {
                _log.Warn($"{processName}: still running after {waited}s.");
                return false;
            }
            Thread.Sleep(1000);
        }
    }

    private void CloseAll(string processName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(5000)) p.Kill();
                    _log.Info($"Closed {processName} (PID {p.Id}).");
                }
                catch (Exception ex) { _log.Warn($"Could not close {processName} (PID {p.Id}): {ex.Message}"); }
            }
        }
    }

    private void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _log.Warn($"Could not remove {dir}: {ex.Message}"); }
    }

    /// <summary>Start <see cref="ProductDefinition.LaunchAfterInstall"/>. Setup runs elevated, so the program inherits that token.</summary>
    public void LaunchAfterInstall(string dir)
    {
        if (string.IsNullOrWhiteSpace(_product.LaunchAfterInstall)) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, _product.LaunchAfterInstall)) { UseShellExecute = true, WorkingDirectory = dir });
            _log.Info(_product.LaunchAfterInstall + " started.");
        }
        catch (Exception ex) { _log.Warn($"{_product.LaunchAfterInstall} could not be started: {ex.Message}"); }
    }
}
