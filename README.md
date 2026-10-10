# Balsoft.Hive.Installer.Windows

A Windows installer for .NET products, built in WPF. A product's `Setup.exe` is a small WPF
project that describes the product and points at its program files; the package does the rest:

- a wizard (welcome, licence, folder, service account, shortcuts, summary, progress, finish) and
  the silent modes `/SILENT` (progress only) and `/VERYSILENT` (no window),
- per-machine install to Program Files, elevated through UAC,
- upgrades in place that keep chosen files (settings) and the service's run-as account, back up
  what they replace and restore it, including restarting the service, when a later step fails,
- a Windows service registered through the product's own executable, stopped before files are
  replaced, with the run-as account checked against Windows before anything is installed,
- Start menu and optional desktop shortcuts, an "Apps & features" entry, and an uninstaller
  (a copy of Setup) that removes exactly what Setup wrote,
- a step-by-step log, passwords masked,
- a command line for update mechanisms and IT scripts (`/SILENT`, `/VERYSILENT`,
  `/SUPPRESSMSGBOXES`, `/NORESTART`, `/LOG=`, `/DIR=`, `/TASKS=`) and documented exit codes.

Requires Windows 10, Windows Server 2016 or later, x64. Targets .NET 8 and .NET 10.

## Use

**1. Setup project** (WPF executable, self-contained single file):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AssemblyName>MyProduct-Setup</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>   <!-- requireAdministrator -->
    <Version>2.3.0</Version>
    <!-- the signed program files; packed and embedded at build time -->
    <InstallerPayloadDirectory>..\publish\payload</InstallerPayloadDirectory>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Balsoft.Hive.Installer.Windows" Version="1.0.0" />
    <Resource Include="Assets\logo.png" />
  </ItemGroup>
</Project>
```

**2. Product definition and entry point:**

```csharp
internal static class Program
{
    private static ProductDefinition CreateProduct() => new()
    {
        AppId = "{866B88B9-EC7C-4C62-AF0A-F27E2DDF060A}",   // never change it
        ProductName = "My Product",
        Publisher = "Contoso GmbH",
        InstallFolder = @"Contoso\My Product",
        MainExecutable = "MyProduct.exe",
        KeepOnUpgrade = new[] { "appsettings.json" },
        CloseBeforeInstall = new[] { "MyProduct.App.exe" },
        LaunchAfterInstall = "MyProduct.App.exe",
        LaunchLabel = "Open My Product",
        Service = new ServiceDefinition
        {
            Name = "MyProduct",
            Executable = "MyProduct.exe",
            InstallArguments = (exists, account, password) => account is null
                ? new[] { "install", "--quiet" }
                : new[] { "install", "--quiet", "--account", account, "--password", password ?? "" },
            UninstallArguments = new[] { "uninstall", "--quiet" },
        },
        Shortcuts = new[] { new ShortcutDefinition { Name = "My Product", Target = "MyProduct.App.exe" } },
        Branding = new InstallerBranding
        {
            Logo = new Uri("pack://application:,,,/Assets/logo.png"),
            Description = "What My Product does, in one paragraph.",
            LicenseText = File.ReadAllText("EULA.txt"),   // or an embedded resource
        },
    };

    [STAThread]
    private static int Main(string[] args)
    {
        // pack:// URIs only parse once WPF has started: create the Application before the definition.
        _ = new System.Windows.Application();
        return InstallerApp.Run(CreateProduct(), args);
    }
}
```

**3. Build:** sign the program files, then `dotnet publish -c Release -r win-x64 --self-contained
-p:PublishSingleFile=true`, then sign the resulting Setup exe. The build packs
`InstallerPayloadDirectory` (top-level files only) with its version (`InstallerPayloadVersion`,
default `$(Version)`) and embeds it. `samples/SampleProduct.Setup` is a complete example.

The service executable must create the service on a fresh install, update it on an upgrade
(the account arguments are then null: Windows keeps the account and its password), and start it.

## Command line

| Switch | Meaning |
|---|---|
| `/SILENT` | Progress window only, no questions |
| `/VERYSILENT` | No window |
| `/SUPPRESSMSGBOXES` | No message boxes in a silent run |
| `/LOG=path` | Log file (default `%TEMP%\Setup Log <date> #nnn.txt`) |
| `/DIR=path` | Install folder for a fresh install |
| `/SvcAccount=DOMAIN\user /SvcPassword=***` | Service run-as account for a fresh install (blank: LocalSystem) |
| `/DESKTOPICON`, `/TASKS=desktopicon` | Create the desktop shortcuts |
| `/UNINSTALL` | Remove the product (what "Apps & features" runs) |
| `/ALLOWDOWNGRADE` | Install even though a newer version is installed (support cases only) |
| `/NORESTART` | Accepted; Setup never restarts Windows |

Exit codes: `0` done, `1` could not start, `2` cancelled, `4` install failed (an upgrade was
rolled back), `7` preparing failed (nothing changed; also when a newer version is installed).

## Versions

Setup compares its version (the payload's) with the installed one (the uninstall entry's
`DisplayVersion`), part by part, so 2.10.0 is newer than 2.9.0:

- nothing installed: install;
- older version installed: update;
- same version installed: repair (the program files are written again, kept files and the
  service account stay);
- newer version installed: Setup stops (exit code 7) and changes nothing, in the wizard and in
  silent runs alike. `/ALLOWDOWNGRADE` overrides this for support cases.

Both versions are written to the Setup log.

## Silent updates

An application updates itself by starting the new Setup as
`Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="..."` and reading its exit code
(0 success, 1 Setup could not start, 2 cancelled, 4 install failed and rolled back, 7 nothing
changed, for example because a newer version is installed).

## Licence

Apache License 2.0. Copyright (c) Balsoft GmbH.
