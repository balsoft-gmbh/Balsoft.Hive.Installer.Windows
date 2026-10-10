using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Balsoft.Hive.Installer.Windows.Wizard;

/// <summary>
/// The installer window. In wizard mode: welcome, licence (when the product has one), folder,
/// service account (fresh installs of a product with a service), shortcuts (when a desktop
/// shortcut is offered), summary, progress, finish. With /SILENT and for an uninstall it opens on
/// the progress page, asks nothing and closes itself. The work runs on a background thread.
/// </summary>
internal sealed partial class WizardWindow : Window
{
    private enum Page { Welcome, License, Folder, Account, Options, Ready, Progress, Finish }

    private readonly ProductDefinition _product;
    private readonly SetupOptions _options;
    private readonly Payload _payload;
    private readonly SetupLog _log;
    private readonly ExistingInstall _existing;
    private readonly string? _uninstallDir;

    private Page _page = Page.Welcome;
    private bool _running;
    private bool _accountConfirmed;
    private string _installDir;

    public InstallerExitCode Result { get; private set; } = InstallerExitCode.Cancelled;

    /// <summary>The folder Setup installed into (for launching the program afterwards).</summary>
    public string InstallDir => _installDir;

    private bool IsUninstall => _uninstallDir is not null;

    private bool IsRepair => !IsUninstall && InstallPlan.Compare(_existing.IsUpgrade ? _existing.Version : null, _payload.Version) == VersionChange.Same;

    public WizardWindow(ProductDefinition product, SetupOptions options, Payload payload, SetupLog log, ExistingInstall existing, string? uninstallDir = null)
    {
        InitializeComponent();
        _product = product;
        _options = options;
        _payload = payload;
        _log = log;
        _existing = existing;
        _uninstallDir = uninstallDir;
        _installDir = uninstallDir ?? options.InstallDir ?? existing.Dir
                      ?? InstallPlan.DefaultInstallDir(product, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        _log.Line += AppendLog;
        ApplyBranding();
        ApplyTexts();
    }

    private void ApplyBranding()
    {
        var b = _product.Branding;
        Title = IsUninstall ? $"{_product.ProductName} Uninstall" : $"{_product.ProductName} Setup";
        HeaderTitle.Text = _product.ProductName;
        FooterNote.Text = _product.Publisher;
        if (TryLoadImage(b.Logo) is { } logo) HeaderLogo.Source = logo; else HeaderLogo.Visibility = Visibility.Collapsed;
        if (TryLoadImage(b.SideImage) is { } side)
        {
            WelcomeImage.Source = side;
            FinishImage.Source = side;
        }
        else
        {
            WelcomeImageColumn.Width = new GridLength(0);
            FinishImageColumn.Width = new GridLength(0);
        }
        try
        {
            var accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(b.AccentColor));
            NextButton.Background = accent;
            NextButton.BorderBrush = accent;
            ProgressBar.Foreground = accent;
        }
        catch (FormatException) { _log.Warn($"Accent colour '{b.AccentColor}' is not a colour; the default is used."); }
    }

    private BitmapImage? TryLoadImage(Uri? uri)
    {
        if (uri is null) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            return img;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            _log.Warn($"Image {uri} could not be loaded: {ex.Message}");
            return null;
        }
    }

    private void ApplyTexts()
    {
        bool upgrade = _existing.IsUpgrade;
        bool repair = IsRepair;
        HeaderSubtitle.Text = IsUninstall ? "Removing " + _product.ProductName
            : repair ? $"Repair version {_payload.Version}"
            : upgrade ? $"Update {_existing.Version} to {_payload.Version}".Replace("  ", " ", StringComparison.Ordinal)
            : $"Version {_payload.Version}";
        WelcomeTitle.Text = (repair ? "Repair " : upgrade ? "Update " : "Install ") + _product.ProductName;
        WelcomeText.Text = repair
            ? $"{_product.ProductName} {_payload.Version} is already installed in {_installDir}. Setup installs the program files again; settings and the service account are kept."
            : upgrade
            ? $"Setup updates {_product.ProductName} in {_installDir} to version {_payload.Version}." +
              (_product.Service is null ? "" : " The service keeps its settings and its run-as account; it is stopped during the update and started again afterwards.")
            : $"Setup installs {_product.ProductName} {_payload.Version}" + (_product.Service is null ? "." : $" and registers the {_product.Service.Name} Windows service.");
        WelcomeDescription.Text = _product.Branding.Description;
        LicenseText.Text = _product.Branding.LicenseText;
        FolderBox.Text = _installDir;
        FolderBox.IsReadOnly = upgrade;
        BrowseButton.IsEnabled = !upgrade;
        FolderHint.Text = upgrade
            ? $"{_product.ProductName} is already installed here; the update goes into the same folder."
            : $"{_product.ProductName} will be installed into this folder.";
        AccountHint.Text = _product.Service?.AccountHint ?? "";
        AccountBox.Text = _options.ServiceAccount ?? "";
        DesktopBox.IsChecked = _options.DesktopIcon || _existing.DesktopShortcut;
        LaunchBox.Content = _product.LaunchLabel;
    }

    /// <summary>/SILENT and uninstall: start right away on the progress page.</summary>
    public void StartUnattended()
    {
        Show(Page.Progress);
        _ = RunAsync();
    }

    // ── navigation ───────────────────────────────────────────────────────────

    private List<Page> Pages()
    {
        var pages = new List<Page> { Page.Welcome };
        if (!string.IsNullOrWhiteSpace(_product.Branding.LicenseText)) pages.Add(Page.License);
        pages.Add(Page.Folder);
        if (_product.Service is { AskForAccount: true } && !_existing.ServiceExists) pages.Add(Page.Account);
        if (_product.Shortcuts.Any(s => s.OfferDesktop)) pages.Add(Page.Options);
        pages.Add(Page.Ready);
        return pages;
    }

    private void Show(Page page)
    {
        _page = page;
        WelcomePage.Visibility = Vis(page == Page.Welcome);
        LicensePage.Visibility = Vis(page == Page.License);
        FolderPage.Visibility = Vis(page == Page.Folder);
        AccountPage.Visibility = Vis(page == Page.Account);
        OptionsPage.Visibility = Vis(page == Page.Options);
        ReadyPage.Visibility = Vis(page == Page.Ready);
        ProgressPage.Visibility = Vis(page == Page.Progress);
        FinishPage.Visibility = Vis(page == Page.Finish);

        BackButton.Visibility = page is Page.Welcome or Page.Progress or Page.Finish ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = page switch { Page.Ready => IsRepair ? "Repair" : _existing.IsUpgrade ? "Update" : "Install", Page.Finish => "Finish", _ => "Next" };
        NextButton.Visibility = page == Page.Progress ? Visibility.Hidden : Visibility.Visible;
        NextButton.IsEnabled = page != Page.License || AcceptLicense.IsChecked == true;
        CancelButton.Visibility = page is Page.Progress or Page.Finish ? Visibility.Hidden : Visibility.Visible;
        if (page == Page.Ready) ReadySummary.Text = Summary();

        static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Move(int step)
    {
        var pages = Pages();
        int i = pages.IndexOf(_page);
        Show(pages[Math.Clamp(i + step, 0, pages.Count - 1)]);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Move(-1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Folder:
                string dir = FolderBox.Text.Trim().TrimEnd('\\');
                if (dir.Length < 4 || !Path.IsPathFullyQualified(dir))
                {
                    MessageBox.Show(this, $"Enter a full folder path, for example C:\\Program Files\\{_product.InstallFolder}.", "Install folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _installDir = dir;
                break;
            case Page.Account:
                if (!CheckAccount()) return;
                break;
            case Page.Ready:
                Show(Page.Progress);
                _ = RunAsync();
                return;
            case Page.Finish:
                if (!IsUninstall && Result == InstallerExitCode.Success && LaunchBox.Visibility == Visibility.Visible && LaunchBox.IsChecked == true)
                    new InstallEngine(_product, _payload, _log, silent: true).LaunchAfterInstall(_installDir);
                Close();
                return;
        }
        Move(+1);
    }

    private void OnLicenseChoice(object sender, RoutedEventArgs e)
    {
        if (NextButton is not null && _page == Page.License) NextButton.IsEnabled = AcceptLicense.IsChecked == true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Install folder" };
        string start = FolderBox.Text.Trim();
        while (start.Length > 3 && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        if (Directory.Exists(start)) dlg.InitialDirectory = start;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = Path.Combine(dlg.FolderName, Path.GetFileName(_product.InstallFolder));
    }

    /// <summary>The credentials are checked with Windows here, so a typo is caught before anything is installed.</summary>
    private bool CheckAccount()
    {
        AccountError.Visibility = Visibility.Collapsed;
        string account = AccountBox.Text.Trim();
        string password = PasswordBox.Password;
        AccountBox.Text = account;
        _accountConfirmed = false;
        if (account.Length == 0)
            return password.Length == 0 || Fail("Enter the service account the password belongs to, or clear the password to run as LocalSystem.");
        if (password.Length == 0) return Fail("Enter the password for the service account, or clear the account to use LocalSystem.");

        Cursor = System.Windows.Input.Cursors.Wait;
        var check = NativeMethods.CheckServiceCredentials(account, password, out string message);
        Cursor = null;
        if (check == CredentialCheck.Bad) return Fail("The service account could not sign in. " + message);
        if (check == CredentialCheck.Unknown)
        {
            if (MessageBox.Show(this, message + "\n\nContinue with these credentials anyway?", "Service account",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;
            _accountConfirmed = true;
        }
        _log.Info($"Service account {account} accepted ({check}).");
        return true;

        bool Fail(string text)
        {
            AccountError.Text = text;
            AccountError.Visibility = Visibility.Visible;
            return false;
        }
    }

    private string Summary()
    {
        var lines = new List<string>
        {
            $"Version: {_payload.Version}" + (_existing.IsUpgrade ? $" (update from {_existing.Version ?? "an earlier version"})" : ""),
            "Folder: " + _installDir,
        };
        if (_product.Service is not null)
            lines.Add("Service account: " + (_existing.ServiceExists ? "unchanged (the existing service keeps its account)"
                : AccountBox.Text.Trim().Length == 0 ? "LocalSystem" : AccountBox.Text.Trim()));
        if (_product.Shortcuts.Count > 0)
            lines.Add("Shortcuts: Start menu" + (DesktopBox.IsChecked == true && _product.Shortcuts.Any(s => s.OfferDesktop) ? " and desktop" : ""));
        if (_product.KeepOnUpgrade.Count > 0 && _existing.IsUpgrade)
            lines.Add("Kept: " + string.Join(", ", _product.KeepOnUpgrade));
        return string.Join("\n", lines);
    }

    // ── run ──────────────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        _running = true;
        ProgressTitle.Text = (IsUninstall ? "Removing " : _existing.IsUpgrade ? "Updating " : "Installing ") + _product.ProductName;
        string? account = _options.IsSilent ? _options.ServiceAccount : AccountBox.Text.Trim();
        string? password = _options.IsSilent ? _options.ServicePassword : PasswordBox.Password;
        var choices = new InstallChoices(_installDir, account, password, DesktopBox.IsChecked == true, _options.AllowDowngrade);
        bool silent = _options.IsSilent;
        bool confirmed = _accountConfirmed;

        (InstallerExitCode result, string message) = await Task.Run(() =>
        {
            if (IsUninstall)
                return new UninstallEngine(_product, _payload, _log) { Progress = ReportProgress }.Run(_uninstallDir!);
            return new InstallEngine(_product, _payload, _log, silent)
            {
                CredentialsAlreadyConfirmed = confirmed,
                Progress = ReportProgress,
                AskYesNo = question => Dispatcher.Invoke(() =>
                    MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes),
            }.Run(_existing, choices);
        }).ConfigureAwait(true);

        _running = false;
        Result = result;
        if (silent)
        {
            if (result != InstallerExitCode.Success && !_options.SuppressMessageBoxes)
                MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
        ShowFinish(result, message);
    }

    private void ShowFinish(InstallerExitCode result, string message)
    {
        bool ok = result == InstallerExitCode.Success;
        string name = _product.ProductName;
        FinishTitle.Text = ok
            ? IsUninstall ? $"{name} was removed" : _existing.IsUpgrade ? "Update complete" : "Installation complete"
            : IsUninstall ? "Uninstall did not complete" : "Setup did not complete";
        FinishText.Text = ok
            ? IsUninstall
                ? $"{name} was removed. {_product.Branding.UninstallKeepsNote}".Trim()
                : _existing.IsUpgrade
                    ? $"{name} {_payload.Version} is installed" + (_product.Service is null ? "." : " and the service is running again.")
                    : $"{name} {_payload.Version} is installed. {_product.Branding.AfterInstallHint}".Trim()
            : message + (_existing.IsUpgrade && !IsUninstall && result == InstallerExitCode.InstallFailed ? "\n\nThe previous version was restored." : "");
        FinishText.Foreground = ok ? (Brush)FindResource("TextSecondary") : (Brush)FindResource("Danger");
        LaunchBox.Visibility = ok && !IsUninstall && !string.IsNullOrWhiteSpace(_product.LaunchAfterInstall) ? Visibility.Visible : Visibility.Collapsed;
        FinishLog.Text = _log.Path is null ? "" : "Setup log: " + _log.Path;
        Show(Page.Finish);
    }

    private void ReportProgress(int percent, string status) => Dispatcher.BeginInvoke(() =>
    {
        ProgressBar.Value = percent;
        ProgressStatus.Text = status;
    });

    private void AppendLog(string line) => Dispatcher.BeginInvoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_running) { e.Cancel = true; return; }   // never stop half-way; the engine restores on failure itself
        if (_page is not (Page.Finish or Page.Progress) && !_options.IsSilent)
        {
            if (MessageBox.Show(this, "Setup is not complete. Exit Setup?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _log.Info("Setup was cancelled by the user before anything was changed.");
        }
        _log.Line -= AppendLog;
    }
}
