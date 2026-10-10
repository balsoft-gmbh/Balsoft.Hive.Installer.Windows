using System.Text;

namespace Balsoft.Hive.Installer.Windows.Tests;

public class InstallerTests
{
    private static ProductDefinition Product(Action<ServiceDefinition?>? _ = null) => new()
    {
        AppId = "{866B88B9-EC7C-4C62-AF0A-F27E2DDF060A}",
        ProductName = "Sample Product",
        Publisher = "Balsoft GmbH",
        PublisherUrl = new Uri("https://balsoft.de"),
        InstallFolder = @"Balsoft\Sample Product",
        MainExecutable = "Sample.exe",
        KeepOnUpgrade = new[] { "appsettings.json" },
        Service = new ServiceDefinition
        {
            Name = "SampleService",
            Executable = "Sample.exe",
            InstallArguments = (exists, account, password) => account is null ? new[] { "install" } : new[] { "install", "--account", account, "--password", password ?? "" },
            UninstallArguments = new[] { "uninstall" },
        },
    };

    // ── command line ─────────────────────────────────────────────────────────

    [Fact]
    public void Silent_update_command_line_is_understood()
    {
        var o = SetupOptions.Parse(new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", @"/LOG=C:\ProgramData\App\logs\setup-v2.3.0.log" });
        Assert.Equal(SetupUi.VerySilent, o.Ui);
        Assert.True(o.SuppressMessageBoxes);
        Assert.Equal(@"C:\ProgramData\App\logs\setup-v2.3.0.log", o.LogPath);
        Assert.Empty(o.Ignored);
        Assert.False(o.Uninstall);
    }

    [Fact]
    public void Switches_are_case_insensitive_and_values_may_be_quoted()
    {
        var o = SetupOptions.Parse(new[] { "/silent", @"/svcaccount=CONTOSO\svc", "/SvcPassword=p a=ss", "/DIR=\"D:\\Apps\\Sample\"", "/TASKS=desktopicon" });
        Assert.Equal(SetupUi.Silent, o.Ui);
        Assert.Equal(@"CONTOSO\svc", o.ServiceAccount);
        Assert.Equal("p a=ss", o.ServicePassword);
        Assert.Equal(@"D:\Apps\Sample", o.InstallDir);
        Assert.True(o.DesktopIcon);
    }

    [Fact]
    public void Very_silent_wins_and_unknown_arguments_are_reported()
    {
        var o = SetupOptions.Parse(new[] { "/VERYSILENT", "/SILENT", "/FOO", "bar", "/TASKS=!desktopicon" });
        Assert.Equal(SetupUi.VerySilent, o.Ui);
        Assert.Equal(new[] { "/FOO", "bar" }, o.Ignored);
        Assert.False(o.DesktopIcon);
    }

    [Fact]
    public void Uninstall_restart_keeps_the_mode()
    {
        var o = SetupOptions.Parse(new[] { "/UNINSTALL", "/VERYSILENT", "/SUPPRESSMSGBOXES", @"/LOG=C:\t\u.log" });
        Assert.Equal(new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", @"/LOG=C:\t\u.log", "/UNINSTALL" }, o.ToArguments());
        var temp = SetupOptions.Parse(o.ToArguments().Append(@"/FROMTEMP=C:\Program Files\Balsoft\Sample Product"));
        Assert.Equal(@"C:\Program Files\Balsoft\Sample Product", temp.FromTempFor);
        Assert.True(temp.Uninstall);
    }

    // ── product definition ───────────────────────────────────────────────────

    [Fact]
    public void A_complete_definition_is_valid()
    {
        Assert.Empty(Product().Validate());
        Assert.Equal("SampleProduct-Setup.exe", Product().EffectiveUninstallerFileName);
    }

    [Fact]
    public void Broken_definitions_are_named()
    {
        var p = new ProductDefinition { AppId = "866B88B9", ProductName = "", Publisher = "P", InstallFolder = @"C:\Abs", MainExecutable = "a.exe" };
        var problems = p.Validate();
        Assert.Contains(problems, x => x.Contains("AppId", StringComparison.Ordinal));
        Assert.Contains(problems, x => x.Contains("ProductName", StringComparison.Ordinal));
        Assert.Contains(problems, x => x.Contains("InstallFolder", StringComparison.Ordinal));
    }

    // ── install plan ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("appsettings.json", true, false)]
    [InlineData("APPSETTINGS.JSON", true, false)]
    [InlineData("appsettings.json", false, true)]
    [InlineData("Sample.exe", true, true)]
    public void Kept_files_survive_upgrades_everything_else_is_replaced(string name, bool exists, bool written)
        => Assert.Equal(written, InstallPlan.ShouldWrite(Product(), name, exists));

    [Fact]
    public void Uninstall_entry_points_at_the_installed_uninstaller()
    {
        var e = InstallPlan.UninstallEntry(Product(), "2.3.0", @"C:\Program Files\Balsoft\Sample Product", 152_000, new DateTime(2026, 10, 10));
        Assert.Equal("Sample Product", e["DisplayName"]);
        Assert.Equal("2.3.0", e["DisplayVersion"]);
        Assert.Equal("\"C:\\Program Files\\Balsoft\\Sample Product\\uninstall\\SampleProduct-Setup.exe\" /UNINSTALL", e["UninstallString"]);
        Assert.EndsWith("/UNINSTALL /VERYSILENT /SUPPRESSMSGBOXES", (string)e["QuietUninstallString"], StringComparison.Ordinal);
        Assert.Equal(@"C:\Program Files\Balsoft\Sample Product\Sample.exe", e["DisplayIcon"]);
        Assert.Equal("20261010", e["InstallDate"]);
        Assert.Equal(152_000, e["EstimatedSize"]);
        Assert.Equal("https://balsoft.de/", e["URLInfoAbout"]);
        Assert.False(e.ContainsKey("HelpLink"));
    }

    [Fact]
    public void Uninstall_removes_the_payload_but_not_its_manifest()
        => Assert.Equal(new[] { "Sample.exe", "appsettings.json" },
            InstallPlan.FilesToRemove(new[] { "Sample.exe", "payload.json", "appsettings.json", "Sample.exe" }));

    // ── payload ──────────────────────────────────────────────────────────────

    [Fact]
    public void Payload_round_trips_with_its_version_and_keeps_existing_settings()
    {
        string root = Path.Combine(Path.GetTempPath(), "hive-installer-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            File.WriteAllText(Path.Combine(src, "Sample.exe"), "program v2");
            File.WriteAllText(Path.Combine(src, "appsettings.json"), "{ \"new\": true }");
            string archive = Path.Combine(root, "payload.tar.br");
            PayloadArchive.Create(src, archive, "2.3.0");

            var payload = Payload.Open(() => File.OpenRead(archive));
            Assert.Equal("2.3.0", payload.Version);
            Assert.Equal(new[] { "Sample.exe", "appsettings.json" }, payload.FileNames.OrderBy(n => n, StringComparer.Ordinal));

            string target = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            File.WriteAllText(Path.Combine(target, "appsettings.json"), "{ \"customer\": true }");
            File.WriteAllText(Path.Combine(target, "Sample.exe"), "program v1");
            var written = new List<string>();
            payload.Extract(target, n => InstallPlan.ShouldWrite(Product(), n, File.Exists(Path.Combine(target, n))), (n, len) => written.Add($"{n}:{(len < 0 ? "kept" : "written")}"));

            Assert.Equal("program v2", File.ReadAllText(Path.Combine(target, "Sample.exe")));
            Assert.Equal("{ \"customer\": true }", File.ReadAllText(Path.Combine(target, "appsettings.json")));
            Assert.Contains("appsettings.json:kept", written);
            Assert.Empty(Directory.GetFiles(target, "*.setup-new"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Payload_folders_with_subfolders_are_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "hive-installer-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "sub"));
            File.WriteAllText(Path.Combine(root, "src", "a.txt"), "a");
            Assert.Throws<InvalidOperationException>(() => PayloadArchive.Create(Path.Combine(root, "src"), Path.Combine(root, "p.tar.br"), "1.0.0"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── pending reboot renames ───────────────────────────────────────────────

    private static byte[] Raw(params string[] items) => Encoding.Unicode.GetBytes(string.Concat(items.Select(i => i + "\0")) + "\0");

    [Fact]
    public void Delete_operations_keep_pairs_aligned()
    {
        var items = PendingRenames.Decode(Raw(@"\??\C:\Other\a.tmp", "", @"\??\C:\Other\b.new", @"!\??\C:\Other\b.dll"));
        Assert.Equal(4, items.Count);
        var pairs = PendingRenames.ToPairs(items);
        Assert.Equal("", pairs[0].Target);
        Assert.Equal(@"!\??\C:\Other\b.dll", pairs[1].Target);
    }

    [Fact]
    public void Only_pairs_touching_the_install_folder_are_dropped()
    {
        string dir = @"C:\Program Files\Balsoft\Sample Product";
        var items = PendingRenames.Decode(Raw(
            @"\??\C:\Other\x.tmp", "",
            @"\??\C:\Program Files\Balsoft\Sample Product\Sample.exe.new", @"!\??\C:\Program Files\Balsoft\Sample Product\Sample.exe",
            @"\??\C:\Program Files\Balsoft\Sample Product Tools\keep.dll", "",
            @"\??\C:\Other\y.new", @"!\??\C:\Other\y.dll"));
        var (keep, drop) = PendingRenames.Split(items, dir);
        Assert.Single(drop);
        Assert.Equal(3, keep.Count);
        var rewritten = PendingRenames.Decode(PendingRenames.Encode(PendingRenames.FromPairs(keep)));
        Assert.Equal(new[] { @"\??\C:\Other\x.tmp", "", @"\??\C:\Program Files\Balsoft\Sample Product Tools\keep.dll", "", @"\??\C:\Other\y.new", @"!\??\C:\Other\y.dll" }, rewritten);
    }

    [Fact]
    public void Encode_and_decode_round_trip_with_a_closing_double_null()
    {
        var items = new List<string> { "a", "", "b", "c" };
        byte[] raw = PendingRenames.Encode(items);
        Assert.Equal("a\0\0b\0c\0\0", Encoding.Unicode.GetString(raw));
        Assert.Equal(items, PendingRenames.Decode(raw));
        Assert.Empty(PendingRenames.Decode(Encoding.Unicode.GetBytes("\0")));
    }

    // ── versions ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "2.3.0", "Fresh")]
    [InlineData("2.2.3", "2.3.0", "Upgrade")]
    [InlineData("2.9.0", "2.10.0", "Upgrade")]
    [InlineData("2.3", "2.3.0", "Same")]
    [InlineData("v2.3.0", "2.3.0", "Same")]
    [InlineData("2.3.0.0", "2.3.0", "Same")]
    [InlineData("2.3.0", "2.3.0-beta", "Same")]
    [InlineData("2.3.0", "2.2.3", "Downgrade")]
    [InlineData("2.10.0", "2.9.9", "Downgrade")]
    [InlineData("unknown", "2.3.0", "Unknown")]
    public void Installed_and_setup_versions_are_compared_numerically(string? installed, string setup, string expected)
        => Assert.Equal(Enum.Parse<VersionChange>(expected), InstallPlan.Compare(installed, setup));

    [Fact]
    public void Downgrade_needs_an_explicit_switch()
    {
        Assert.False(SetupOptions.Parse(new[] { "/VERYSILENT" }).AllowDowngrade);
        Assert.True(SetupOptions.Parse(new[] { "/VERYSILENT", "/allowdowngrade" }).AllowDowngrade);
    }

    [Fact]
    public void Downgrade_message_names_both_versions_and_the_way_out()
    {
        string m = InstallEngine.DowngradeMessage(Product(), "2.3.0", "2.2.3");
        Assert.Contains("2.3.0 is installed", m, StringComparison.Ordinal);
        Assert.Contains("(2.2.3)", m, StringComparison.Ordinal);
        Assert.Contains("/ALLOWDOWNGRADE", m, StringComparison.Ordinal);
    }
}
