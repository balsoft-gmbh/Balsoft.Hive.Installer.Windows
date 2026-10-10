using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>
/// The payload format: a tar archive of the program files plus <c>payload.json</c>
/// (<c>{"version":"x.y.z"}</c>), compressed with Brotli. Written at build time by the packer
/// (this file is compiled into it too) and read by Setup from its embedded resource
/// <c>payload.tar.br</c>. Only plain files at the top level; subfolders are not supported.
/// </summary>
internal static class PayloadArchive
{
    public const string ResourceName = "payload.tar.br";
    public const string ManifestName = "payload.json";

    /// <summary>One file in the archive, delivered while it is read.</summary>
    public delegate void FileHandler(string name, long length, Stream content);

    /// <summary>Pack every file of <paramref name="sourceDir"/> (no subfolders) with the version manifest.</summary>
    public static void Create(string sourceDir, string outputFile, string version)
    {
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException($"Payload folder {sourceDir} does not exist.");
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("A version is required.", nameof(version));
        var files = Directory.GetFiles(sourceDir);
        if (files.Length == 0) throw new InvalidOperationException($"Payload folder {sourceDir} is empty.");
        if (Directory.GetDirectories(sourceDir).Length > 0)
            throw new InvalidOperationException($"Payload folder {sourceDir} contains subfolders; only files at the top level are installed.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
        using var output = File.Create(outputFile);
        using var brotli = new BrotliStream(output, CompressionLevel.SmallestSize);
        using var tar = new TarWriter(brotli, TarEntryFormat.Pax, leaveOpen: false);

        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { version });
        using (var ms = new MemoryStream(manifest))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, ManifestName) { DataStream = ms });

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            string name = Path.GetFileName(f);
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            using var fs = File.OpenRead(f);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = fs });
        }
    }

    /// <summary>Read the archive once; <paramref name="onFile"/> sees every program file (not the manifest). Returns the version.</summary>
    public static string Read(Stream compressed, FileHandler? onFile)
    {
        string? version = null;
        using var brotli = new BrotliStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(brotli);
        while (tar.GetNextEntry(copyData: false) is { } e)
        {
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || e.DataStream is null) continue;
            string name = Path.GetFileName(e.Name);
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(e.DataStream);
                version = doc.RootElement.GetProperty("version").GetString();
                continue;
            }
            onFile?.Invoke(name, e.Length, e.DataStream);
        }
        return string.IsNullOrWhiteSpace(version)
            ? throw new InvalidDataException("The payload has no version (payload.json).")
            : version;
    }

    /// <summary>The names and total size of the program files, and the version, without extracting.</summary>
    public static (string Version, IReadOnlyList<string> Names, long TotalBytes) Inspect(Stream compressed)
    {
        var names = new List<string>();
        long total = 0;
        string version = Read(compressed, (name, length, _) => { names.Add(name); total += length; });
        return (version, names, total);
    }
}
