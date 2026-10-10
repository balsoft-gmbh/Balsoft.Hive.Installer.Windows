using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using Balsoft.Hive.Installer.Windows.Wizard;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>
/// Entry point of a product's Setup. The Setup project is a WPF executable whose <c>Main</c> is:
/// <code>
/// [STAThread]
/// static int Main(string[] args) => InstallerApp.Run(MyProduct.Definition, args);
/// </code>
/// It reads the command line, then installs, upgrades or uninstalls in the requested mode and
/// returns an <see cref="InstallerExitCode"/> as the process exit code.
/// </summary>
public static class InstallerApp
{
    /// <summary>Run Setup for <paramref name="product"/> with the given command line; returns the exit code.</summary>
    public static int Run(ProductDefinition product, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(args);
        var app = Application.Current ?? new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var session = new Session(product, args, app);
        app.Startup += (_, _) => session.Start();
        return app.Run();
    }

    private sealed class Session(ProductDefinition product, IReadOnlyList<string> args, Application app) : IDisposable
    {
        private readonly SetupOptions _options = SetupOptions.Parse(args);
        private SetupLog? _log;
        private Mutex? _single;
        private bool _ownsMutex;

        public void Start()
        {
            _log = new SetupLog(_options.LogPath ?? SetupLog.DefaultPath());
            _log.Info($"{product.ProductName} Setup started: {string.Join(' ', Mask(args))}");
            foreach (var ignored in _options.Ignored) _log.Warn("Unknown argument ignored: " + ignored);

            var problems = product.Validate();
            if (problems.Count > 0)
            {
                Fail("This Setup is built wrongly: " + string.Join(" ", problems), InstallerExitCode.InitFailed);
                return;
            }

            _single = new Mutex(true, @"Global\HiveInstaller-" + product.AppId, out _ownsMutex);
            if (!_ownsMutex)
            {
                Fail($"{product.ProductName} Setup is already running.", InstallerExitCode.InitFailed);
                return;
            }

            Payload payload;
            try
            {
                payload = Payload.OpenEmbedded();
                _log.Info($"Setup for {product.ProductName} {payload.Version}: {payload.FileNames.Count} file(s), {payload.TotalBytes:N0} bytes.");
            }
            catch (Exception ex)
            {
                Fail(ex.Message, InstallerExitCode.InitFailed);
                return;
            }

            if (_options.Uninstall) Uninstall(payload);
            else Install(payload);
        }

        private string DefaultDir(ExistingInstall existing)
            => _options.InstallDir ?? existing.Dir
               ?? InstallPlan.DefaultInstallDir(product, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

        private void Install(Payload payload)
        {
            ExistingInstall existing;
            try { existing = InstallEngine.Detect(product); }
            catch (Exception ex) { Fail("Setup could not read the current installation: " + ex.Message, InstallerExitCode.InitFailed); return; }

            // A newer installed version is refused before any window opens (the engine checks again).
            if (InstallPlan.Compare(existing.IsUpgrade ? existing.Version : null, payload.Version) == VersionChange.Downgrade && !_options.AllowDowngrade)
            {
                Fail(InstallEngine.DowngradeMessage(product, existing.Version!, payload.Version), InstallerExitCode.PrepareFailed);
                return;
            }

            if (_options.Ui == SetupUi.VerySilent)
            {
                string dir = DefaultDir(existing);
                var engine = new InstallEngine(product, payload, _log!, silent: true);
                var (result, message) = engine.Run(existing,
                    new InstallChoices(dir, _options.ServiceAccount, _options.ServicePassword, _options.DesktopIcon || existing.DesktopShortcut, _options.AllowDowngrade));
                if (result == InstallerExitCode.Success) engine.LaunchAfterInstall(dir);   // an application's own update shows the new version right away
                else ShowError(message);
                Finish(result);
                return;
            }

            var window = new WizardWindow(product, _options, payload, _log!, existing);
            window.Closed += (_, _) =>
            {
                if (_options.IsSilent && window.Result == InstallerExitCode.Success)
                    new InstallEngine(product, payload, _log!, silent: true).LaunchAfterInstall(window.InstallDir);
                Finish(window.Result);
            };
            window.Show();
            if (_options.IsSilent) window.StartUnattended();
        }

        private void Uninstall(Payload payload)
        {
            // Started from {app}\uninstall: move to %TEMP% first; the copy does the work.
            if (_options.FromTempFor is null)
            {
                string? dir = UninstallEngine.ResolveInstallDir(product);
                if (dir is null) { Fail($"{product.ProductName} is not installed on this computer.", InstallerExitCode.InitFailed); return; }
                Finish(UninstallEngine.RelaunchFromTemp(_options, dir, _log!) ? InstallerExitCode.Success : InstallerExitCode.InitFailed);
                return;
            }

            string installDir = _options.FromTempFor;
            if (!_options.IsSilent && MessageBox.Show(
                    $"Remove {product.ProductName} from this computer?" +
                    (string.IsNullOrWhiteSpace(product.Branding.UninstallKeepsNote) ? "" : "\n\n" + product.Branding.UninstallKeepsNote),
                    product.ProductName + " Uninstall", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                _log!.Info("Uninstall cancelled by the user.");
                UninstallEngine.ScheduleSelfCleanup(_log);
                Finish(InstallerExitCode.Cancelled);
                return;
            }

            if (_options.Ui == SetupUi.VerySilent)
            {
                var (result, message) = new UninstallEngine(product, payload, _log!).Run(installDir);
                if (result != InstallerExitCode.Success) ShowError(message);
                UninstallEngine.ScheduleSelfCleanup(_log!);
                Finish(result);
                return;
            }

            var window = new WizardWindow(product, _options, payload, _log!, new ExistingInstall(installDir, null, product.Service is not null, false), installDir);
            window.Closed += (_, _) => { UninstallEngine.ScheduleSelfCleanup(_log!); Finish(window.Result); };
            window.Show();
            window.StartUnattended();
        }

        private void Fail(string message, InstallerExitCode result)
        {
            _log?.Error(message);
            ShowError(message);
            Finish(result);
        }

        /// <summary>/SUPPRESSMSGBOXES only applies to a silent run.</summary>
        private void ShowError(string message)
        {
            if (!(_options.IsSilent && _options.SuppressMessageBoxes))
                MessageBox.Show(message, product.ProductName + " Setup", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void Finish(InstallerExitCode result)
        {
            _log?.Info($"Setup exit code {(int)result} ({result}).");
            Dispose();
            app.Shutdown((int)result);
        }

        public void Dispose()
        {
            _log?.Dispose();
            _log = null;
            if (_ownsMutex) _single?.ReleaseMutex();
            _ownsMutex = false;
            _single?.Dispose();
            _single = null;
        }

        /// <summary>The command line for the log, with the service password hidden.</summary>
        private static IEnumerable<string> Mask(IEnumerable<string> a)
            => a.Select(x => x.TrimStart('/', '-').StartsWith("SvcPassword", StringComparison.OrdinalIgnoreCase) ? "/SvcPassword=***" : x);
    }
}
