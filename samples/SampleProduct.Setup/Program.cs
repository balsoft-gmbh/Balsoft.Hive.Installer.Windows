using System;
using Balsoft.Hive.Installer.Windows;

namespace SampleProduct.Setup;

internal static class Program
{
    private static ProductDefinition CreateProduct() => new()
    {
        AppId = "{3F0B6C1E-6B7A-4D52-9C1E-5A2B7D9E4F10}",
        ProductName = "Sample Product",
        Publisher = "Balsoft GmbH",
        PublisherUrl = new Uri("https://balsoft.de"),
        InstallFolder = @"Balsoft\Sample Product",
        MainExecutable = "Sample.cmd",
        KeepOnUpgrade = new[] { "settings.json" },
        Shortcuts = new[] { new ShortcutDefinition { Name = "Sample Product", Target = "Sample.cmd", Description = "Sample Product" } },
        Branding = new InstallerBranding
        {
            Logo = new Uri("pack://application:,,,/Assets/logo.png"),
            Description = "A sample product that shows how a Setup is built with Balsoft.Hive.Installer.Windows.",
            LicenseText = "Sample licence text. Replace it with the product's licence agreement.",
            UninstallKeepsNote = "Nothing outside the install folder is touched.",
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
