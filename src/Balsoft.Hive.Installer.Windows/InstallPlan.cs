using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>How the version in Setup relates to the installed one.</summary>
internal enum VersionChange
{
    /// <summary>Nothing installed.</summary>
    Fresh,
    /// <summary>Setup carries a newer version.</summary>
    Upgrade,
    /// <summary>The same version is installed; Setup repairs it.</summary>
    Same,
    /// <summary>A newer version is installed; Setup would downgrade it.</summary>
    Downgrade,
    /// <summary>The installed version cannot be read; treated like an upgrade.</summary>
    Unknown,
}

/// <summary>
/// The fixed facts of an installation for one <see cref="ProductDefinition"/>: registry names,
/// folders, which payload files are replaced or kept and the "Apps &amp; features" entry.
/// No Windows calls, so it is unit-tested.
/// </summary>
internal static class InstallPlan
{
    public const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Folder inside the install folder that holds the uninstaller (a copy of Setup).</summary>
    public const string UninstallFolder = "uninstall";

    /// <summary>The payload's own manifest, never installed.</summary>
    public const string PayloadManifest = "payload.json";

    public static string UninstallKeyName(ProductDefinition product) => product.AppId;

    public static string DefaultInstallDir(ProductDefinition product, string programFiles) => Path.Combine(programFiles, product.InstallFolder);

    public static string UninstallerPath(ProductDefinition product, string installDir)
        => Path.Combine(installDir, UninstallFolder, product.EffectiveUninstallerFileName);

    /// <summary>Whether a payload file is written: files in <see cref="ProductDefinition.KeepOnUpgrade"/> survive every upgrade, everything else is replaced.</summary>
    public static bool ShouldWrite(ProductDefinition product, string payloadName, bool existsInTarget)
        => !(existsInTarget && product.KeepOnUpgrade.Contains(payloadName, StringComparer.OrdinalIgnoreCase));

    /// <summary>Files an uninstall removes: everything Setup writes, kept settings included.</summary>
    public static IEnumerable<string> FilesToRemove(IEnumerable<string> payloadNames)
        => payloadNames.Where(n => !n.Equals(PayloadManifest, StringComparison.OrdinalIgnoreCase))
                       .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compare the installed version with Setup's. Numbers are compared part by part (2.10.0 is newer
    /// than 2.9.0); a leading "v", a pre-release or build suffix (2.3.0-beta, 2.3.0+abc) and missing
    /// parts (2.3 = 2.3.0) are tolerated.
    /// </summary>
    public static VersionChange Compare(string? installed, string setup)
    {
        if (installed is null) return VersionChange.Fresh;
        var a = ParseVersion(installed);
        var b = ParseVersion(setup);
        if (a is null || b is null) return VersionChange.Unknown;
        int c = b.CompareTo(a);
        return c > 0 ? VersionChange.Upgrade : c == 0 ? VersionChange.Same : VersionChange.Downgrade;
    }

    /// <summary>Where a pre-release or build suffix starts (2.3.0-beta, 2.3.0+abc).</summary>
    private static readonly SearchValues<char> VersionSuffixStart = SearchValues.Create("-+ ");

    private static Version? ParseVersion(string text)
    {
        string v = text.Trim().TrimStart('v', 'V');
        int cut = v.AsSpan().IndexOfAny(VersionSuffixStart);
        if (cut >= 0) v = v[..cut];
        if (!Version.TryParse(v.Contains('.', StringComparison.Ordinal) ? v : v + ".0", out var parsed)) return null;
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }

    /// <summary>The values of the "Apps &amp; features" entry.</summary>
    public static IReadOnlyDictionary<string, object> UninstallEntry(ProductDefinition product, string version, string installDir, long estimatedSizeKb, DateTime installDate)
    {
        string uninstaller = UninstallerPath(product, installDir);
        var entry = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DisplayName"] = product.EffectiveDisplayName,
            ["DisplayVersion"] = version,
            ["Publisher"] = product.Publisher,
            ["InstallLocation"] = installDir,
            ["DisplayIcon"] = Path.Combine(installDir, product.MainExecutable),
            ["UninstallString"] = $"\"{uninstaller}\" /UNINSTALL",
            ["QuietUninstallString"] = $"\"{uninstaller}\" /UNINSTALL /VERYSILENT /SUPPRESSMSGBOXES",
            ["InstallDate"] = installDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            ["EstimatedSize"] = (int)Math.Clamp(estimatedSizeKb, 0, int.MaxValue),
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
        };
        if (product.PublisherUrl is not null) entry["URLInfoAbout"] = product.PublisherUrl.ToString();
        if (product.HelpUrl is not null) entry["HelpLink"] = product.HelpUrl.ToString();
        return entry;
    }
}
