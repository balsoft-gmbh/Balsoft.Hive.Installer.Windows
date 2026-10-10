using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>
/// Removes a product: its service, the program files Setup wrote, the shortcuts and the uninstall
/// entry. Anything else (data folders, files the customer added) stays.
///
/// The uninstaller is <c>{app}\uninstall\{Product}-Setup.exe</c>, inside the folder it removes. So it
/// first copies itself to %TEMP% and restarts from there (<see cref="RelaunchFromTemp"/>); the copy
/// does the work and deletes its own temp folder at the end.
/// </summary>
internal sealed class UninstallEngine
{
    private const string TempPrefix = "HiveInstallerUninstall-";

    private readonly ProductDefinition _product;
    private readonly Payload _payload;
    private readonly SetupLog _log;
    private readonly InstallEngine _helper;

    public Action<int, string> Progress { get; init; } = (_, _) => { };

    public UninstallEngine(ProductDefinition product, Payload payload, SetupLog log)
    {
        _product = product;
        _payload = payload;
        _log = log;
        _helper = new InstallEngine(product, payload, log, silent: true);
    }

    /// <summary>The install folder this uninstaller belongs to: two levels above {app}\uninstall\Setup.exe, else the registry.</summary>
    public static string? ResolveInstallDir(ProductDefinition product)
    {
        string? self = Environment.ProcessPath;
        string? parent = self is null ? null : Path.GetDirectoryName(self);
        if (parent is not null && Path.GetFileName(parent).Equals(InstallPlan.UninstallFolder, StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(parent);
        return InstallEngine.Detect(product).Dir;
    }

    /// <summary>Copy this exe to %TEMP% and start it there with /FROMTEMP; false when that was not possible.</summary>
    public static bool RelaunchFromTemp(SetupOptions options, string installDir, SetupLog log)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            string copy = Path.Combine(tempDir, Path.GetFileName(Environment.ProcessPath!));
            File.Copy(Environment.ProcessPath!, copy);
            var psi = new ProcessStartInfo(copy) { UseShellExecute = false, WorkingDirectory = tempDir };
            foreach (var a in options.ToArguments()) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("/FROMTEMP=" + installDir);
            using var p = Process.Start(psi);
            log.Info("Uninstaller restarted from " + copy);
            return true;
        }
        catch (Exception ex)
        {
            log.Error("The uninstaller could not restart from %TEMP%: " + ex.Message);
            return false;
        }
    }

    public (InstallerExitCode Result, string Message) Run(string installDir)
    {
        string dir = installDir.TrimEnd('\\');
        _log.Info($"Uninstalling {_product.ProductName} from {dir}.");
        try
        {
            foreach (var exe in _product.CloseBeforeInstall)
            {
                Progress(10, $"Waiting for {exe} to close...");
                if (!_helper.WaitForExit(Path.GetFileNameWithoutExtension(exe), 15))
                    return (InstallerExitCode.PrepareFailed, $"{exe} is still running. Close it and run the uninstall again.");
            }

            if (_product.Service is not null)
            {
                Progress(25, $"Removing the {_product.Service.Name} service...");
                RemoveService(dir, _product.Service);
            }

            Progress(55, "Removing the program files...");
            RemoveFiles(dir);

            Progress(80, "Removing shortcuts...");
            foreach (var s in _product.Shortcuts)
            {
                TryDelete(InstallEngine.StartMenuShortcutPath(s), "shortcut");
                TryDelete(InstallEngine.DesktopShortcutPath(s), "shortcut");
            }

            Progress(90, "Removing the uninstall entry...");
            using (var hklm = NativeMethods.LocalMachine64())
            {
                hklm.DeleteSubKeyTree($@"{InstallPlan.UninstallRoot}\{InstallPlan.UninstallKeyName(_product)}", throwOnMissingSubKey: false);
            }

            Progress(100, $"{_product.ProductName} was removed.");
            _log.Info("Uninstall finished.");
            return (InstallerExitCode.Success, "");
        }
        catch (Exception ex)
        {
            _log.Error("Uninstall failed: " + ex.Message);
            return (InstallerExitCode.InstallFailed, ex.Message);
        }
    }

    private void RemoveService(string dir, ServiceDefinition service)
    {
        if (!File.Exists(Path.Combine(dir, service.Executable)))
        {
            _log.Warn($"{service.Executable} not found; the {service.Name} service is not unregistered.");
            return;
        }
        int code = InstallEngine.RunTool(dir, service.Executable, service.UninstallArguments, TimeSpan.FromMinutes(2), _log, null);
        if (code != 0) _log.Warn($"Unregistering the service returned exit code {code}.");
        _helper.WaitForExit(Path.GetFileNameWithoutExtension(service.Executable), 15);
    }

    /// <summary>Only what Setup wrote; anything else in the folder stays, and so does the folder then.</summary>
    private void RemoveFiles(string dir)
    {
        foreach (var name in InstallPlan.FilesToRemove(_payload.FileNames))
            TryDelete(Path.Combine(dir, name), "file");
        if (Directory.Exists(dir))
            foreach (var leftover in Directory.EnumerateFiles(dir, "*.setup-new").ToList())
                TryDelete(leftover, "file");
        foreach (var sub in new[] { InstallPlan.UninstallFolder, ".setup-backup" })
        {
            string d = Path.Combine(dir, sub);
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch (Exception ex) { _log.Warn($"Could not remove {d}: {ex.Message}"); }
        }
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            else if (Directory.Exists(dir)) _log.Info($"{dir} still holds files that Setup did not install; the folder is kept.");
            string? parent = Path.GetDirectoryName(dir);   // the publisher folder, when nothing else is in it
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (parent is not null && !parent.Equals(programFiles, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception ex) { _log.Warn("Could not remove the install folder: " + ex.Message); }
    }

    private void TryDelete(string path, string what)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            _log.Info($"Removed {what} {path}");
        }
        catch (Exception ex) { _log.Warn($"Could not remove {what} {path}: {ex.Message}"); }
    }

    /// <summary>Delete this temp copy after it exits (a running exe cannot delete itself).</summary>
    public static void ScheduleSelfCleanup(SetupLog log)
    {
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (dir is null || !Path.GetFileName(dir).StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            // A raw command line: cmd does not understand .NET's escaping of the inner quotes.
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 5 127.0.0.1 >nul & rmdir /s /q \"{dir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { log.Warn($"The temporary uninstaller in {dir} could not be scheduled for deletion: {ex.Message}"); }
    }
}
