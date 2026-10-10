using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>
/// The program files embedded in the product's Setup (resource <c>payload.tar.br</c>, added at
/// build time by the package's build targets from <c>InstallerPayloadDirectory</c>).
/// </summary>
internal sealed class Payload
{
    private readonly Func<Stream> _open;

    public string Version { get; }
    public IReadOnlyList<string> FileNames { get; }
    public long TotalBytes { get; }

    private Payload(Func<Stream> open, string version, IReadOnlyList<string> names, long total)
    {
        _open = open;
        Version = version;
        FileNames = names;
        TotalBytes = total;
    }

    /// <summary>The payload embedded in the Setup executable (the entry assembly).</summary>
    public static Payload OpenEmbedded()
    {
        var assembly = Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("Setup has no entry assembly.");
        Stream OpenResource() => assembly.GetManifestResourceStream(PayloadArchive.ResourceName)
                                 ?? throw new InvalidOperationException("This Setup contains no program files (payload missing). Download it again.");
        return Open(OpenResource);
    }

    /// <summary>A payload from any source; used by the tests.</summary>
    public static Payload Open(Func<Stream> open)
    {
        using var s = open();
        var (version, names, total) = PayloadArchive.Inspect(s);
        return new Payload(open, version, names, total);
    }

    /// <summary>
    /// Write the files into <paramref name="targetDir"/>. <paramref name="shouldWrite"/> decides per
    /// file (kept settings on an upgrade). Each file goes to a temporary name first and is then moved
    /// over the old one, so a failure never leaves a half-written file behind.
    /// </summary>
    public void Extract(string targetDir, Func<string, bool> shouldWrite, Action<string, long> written)
    {
        Directory.CreateDirectory(targetDir);
        using var s = _open();
        PayloadArchive.Read(s, (name, length, content) =>
        {
            string target = Path.Combine(targetDir, name);
            if (!shouldWrite(name)) { written(name, -1); return; }
            string temp = target + ".setup-new";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                content.CopyTo(fs);
            MoveWithRetry(temp, target);
            written(name, length);
        });
    }

    /// <summary>Move over the old file; an antivirus scan can hold a fresh file for a moment, so retry briefly.</summary>
    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(from, to, overwrite: true); return; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(500); }
        }
    }
}
