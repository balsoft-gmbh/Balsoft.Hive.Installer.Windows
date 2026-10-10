using System;
using System.Collections.Generic;
using System.Linq;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>
/// Everything the installer needs to know about one product. A product's Setup project creates
/// one instance and passes it to <see cref="InstallerApp.Run"/>; the program files themselves come
/// from the payload embedded at build time (see the package README).
/// </summary>
public sealed class ProductDefinition
{
    /// <summary>Stable product identity, a GUID in braces, for example <c>{A536E22D-D00A-4FEF-A717-05E2FDC20F9A}</c>. Names the uninstall entry; never change it between versions.</summary>
    public required string AppId { get; init; }

    /// <summary>Short product name, for example "Hive BDC". Used in titles and messages.</summary>
    public required string ProductName { get; init; }

    /// <summary>Name in "Apps &amp; features"; defaults to <see cref="ProductName"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Publisher shown in "Apps &amp; features", for example "Balsoft GmbH".</summary>
    public required string Publisher { get; init; }

    /// <summary>Optional publisher web site ("Apps &amp; features" link).</summary>
    public Uri? PublisherUrl { get; init; }

    /// <summary>Optional support web site ("Apps &amp; features" help link).</summary>
    public Uri? HelpUrl { get; init; }

    /// <summary>Install folder below Program Files, for example <c>Balsoft\Hive BDC</c>.</summary>
    public required string InstallFolder { get; init; }

    /// <summary>Program file whose icon "Apps &amp; features" shows, for example <c>HiveBDC.exe</c>.</summary>
    public required string MainExecutable { get; init; }

    /// <summary>File name of the uninstaller copy in <c>{app}\uninstall</c>; defaults to the product name without spaces plus "-Setup.exe".</summary>
    public string? UninstallerFileName { get; init; }

    /// <summary>Payload files written on a fresh install only and kept on every upgrade, for example <c>appsettings.json</c>.</summary>
    public IReadOnlyCollection<string> KeepOnUpgrade { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Programs (exe file names) that must not run while files are replaced, for example the
    /// product's own desktop app. Setup waits up to 30 seconds for them to exit; in the wizard it
    /// then offers to close them, in a silent run it stops with an error.
    /// </summary>
    public IReadOnlyCollection<string> CloseBeforeInstall { get; init; } = Array.Empty<string>();

    /// <summary>The Windows service the product runs as, if any.</summary>
    public ServiceDefinition? Service { get; init; }

    /// <summary>Shortcuts to create. Start menu shortcuts are always created, desktop shortcuts only when the user asks for them.</summary>
    public IReadOnlyCollection<ShortcutDefinition> Shortcuts { get; init; } = Array.Empty<ShortcutDefinition>();

    /// <summary>Program file started after a successful install (offered on the finish page, started automatically after a silent update), if any.</summary>
    public string? LaunchAfterInstall { get; init; }

    /// <summary>Label of the finish-page check box for <see cref="LaunchAfterInstall"/>.</summary>
    public string LaunchLabel { get; init; } = "Open the application";

    /// <summary>Texts and images of the wizard.</summary>
    public InstallerBranding Branding { get; init; } = new();

    internal string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? ProductName : DisplayName!;

    internal string EffectiveUninstallerFileName =>
        string.IsNullOrWhiteSpace(UninstallerFileName) ? new string(ProductName.Where(c => !char.IsWhiteSpace(c)).ToArray()) + "-Setup.exe" : UninstallerFileName!;

    /// <summary>Problems that make the definition unusable; empty when it is fine.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Guid.TryParse(AppId?.Trim('{', '}'), out _) || !(AppId?.StartsWith('{') ?? false) || !(AppId?.EndsWith('}') ?? false))
            problems.Add("AppId must be a GUID in braces, for example {A536E22D-D00A-4FEF-A717-05E2FDC20F9A}.");
        if (string.IsNullOrWhiteSpace(ProductName)) problems.Add("ProductName is required.");
        if (string.IsNullOrWhiteSpace(Publisher)) problems.Add("Publisher is required.");
        if (string.IsNullOrWhiteSpace(InstallFolder) || System.IO.Path.IsPathRooted(InstallFolder))
            problems.Add("InstallFolder must be a folder below Program Files, for example Balsoft\\Product.");
        if (string.IsNullOrWhiteSpace(MainExecutable)) problems.Add("MainExecutable is required.");
        if (Service is not null)
        {
            if (string.IsNullOrWhiteSpace(Service.Name)) problems.Add("Service.Name is required.");
            if (string.IsNullOrWhiteSpace(Service.Executable)) problems.Add("Service.Executable is required.");
        }
        foreach (var s in Shortcuts)
            if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Target))
                problems.Add("Every shortcut needs a Name and a Target.");
        return problems;
    }
}

/// <summary>
/// A Windows service that the product's own executable registers. Setup stops it before files are
/// replaced and then runs <see cref="Executable"/> with <see cref="InstallArguments"/>, which must
/// create the service on a fresh install, update it on an upgrade, and start it.
/// </summary>
public sealed class ServiceDefinition
{
    /// <summary>Service name, for example "HiveBDC".</summary>
    public required string Name { get; init; }

    /// <summary>Payload file that registers and unregisters the service, for example "HiveBDC.exe".</summary>
    public required string Executable { get; init; }

    /// <summary>
    /// Command line for registering: (service already exists, run-as account or null for
    /// LocalSystem, password) to arguments. On an upgrade the account is null, because the
    /// existing service keeps its account and Windows keeps its password.
    /// </summary>
    public required Func<bool, string?, string?, IReadOnlyList<string>> InstallArguments { get; init; }

    /// <summary>Command line for unregistering, for example ["uninstall", "--quiet"].</summary>
    public required IReadOnlyList<string> UninstallArguments { get; init; }

    /// <summary>Ask for a run-as account on a fresh install (blank = LocalSystem). Default true.</summary>
    public bool AskForAccount { get; init; } = true;

    /// <summary>Explanation on the service account page.</summary>
    public string AccountHint { get; init; } =
        "Leave both fields empty to run the service as LocalSystem. A domain account (DOMAIN\\user) is needed when the service works with network shares. The account is checked with Windows before anything is installed.";
}

/// <summary>A shortcut to a payload file.</summary>
public sealed class ShortcutDefinition
{
    /// <summary>Shortcut name without ".lnk", for example "Hive BDC Dashboard".</summary>
    public required string Name { get; init; }

    /// <summary>Payload file the shortcut starts.</summary>
    public required string Target { get; init; }

    /// <summary>Tooltip text.</summary>
    public string Description { get; init; } = "";

    /// <summary>Offer a desktop shortcut too (unchecked by default, remembered on upgrades).</summary>
    public bool OfferDesktop { get; init; } = true;
}

/// <summary>Texts and images of the wizard. Images are pack URIs into the product's Setup assembly.</summary>
public sealed class InstallerBranding
{
    /// <summary>Square logo in the header, for example <c>pack://application:,,,/Assets/logo.png</c>.</summary>
    public Uri? Logo { get; init; }

    /// <summary>Tall image on the welcome and finish pages (about 164 x 314).</summary>
    public Uri? SideImage { get; init; }

    /// <summary>What the product does, one short paragraph on the welcome page.</summary>
    public string Description { get; init; } = "";

    /// <summary>Licence agreement text the user must accept; no licence page when empty.</summary>
    public string LicenseText { get; init; } = "";

    /// <summary>Extra sentence on the finish page of a fresh install, for example what to configure next.</summary>
    public string AfterInstallHint { get; init; } = "";

    /// <summary>What an uninstall keeps, shown in the confirmation, for example the data folder.</summary>
    public string UninstallKeepsNote { get; init; } = "";

    /// <summary>Accent colour of buttons and the progress bar, as #RRGGBB.</summary>
    public string AccentColor { get; init; } = "#2563EB";
}
