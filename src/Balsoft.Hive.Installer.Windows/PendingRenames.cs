using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>
/// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations: pairs of
/// (source, target) that Windows applies at the next reboot; an empty target deletes the source.
/// A swap that an earlier, interrupted update staged for the install folder would land ON TOP of
/// the files installed now (a downgrade at the next reboot), so this installer drops those pairs.
/// Every other program's pairs are written back unchanged.
///
/// The value is read and written as raw REG_MULTI_SZ bytes: .NET's RegistryKey treats an empty
/// string as the end of the list, which would cut the list at the first delete operation and
/// shift every pair after it.
/// </summary>
internal static class PendingRenames
{
    public sealed record Pair(string Source, string Target);

    /// <summary>Raw REG_MULTI_SZ (UTF-16LE) to strings, keeping empty entries.</summary>
    public static List<string> Decode(byte[] raw)
    {
        string all = Encoding.Unicode.GetString(raw);
        // The list ends with an empty string plus the final terminator ("...\0\0").
        while (all.EndsWith('\0')) all = all[..^1];
        return all.Length == 0 ? new List<string>() : all.Split('\0').ToList();
    }

    /// <summary>Strings to raw REG_MULTI_SZ, each null-terminated, plus the closing null.</summary>
    public static byte[] Encode(IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        foreach (var s in items) sb.Append(s).Append('\0');
        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    public static List<Pair> ToPairs(IReadOnlyList<string> items)
    {
        var pairs = new List<Pair>();
        for (int i = 0; i < items.Count; i += 2)
            pairs.Add(new Pair(items[i], i + 1 < items.Count ? items[i + 1] : ""));
        return pairs;
    }

    public static List<string> FromPairs(IEnumerable<Pair> pairs)
        => pairs.SelectMany(p => new[] { p.Source, p.Target }).ToList();

    /// <summary>Split into the pairs to keep and the ones that touch <paramref name="installDir"/>.</summary>
    public static (List<Pair> Keep, List<Pair> Drop) Split(IReadOnlyList<string> items, string installDir)
    {
        string dir = installDir.TrimEnd('\\') + "\\";
        bool Touches(string path) => StripPrefix(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                                     || StripPrefix(path).Equals(installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var keep = new List<Pair>();
        var drop = new List<Pair>();
        foreach (var p in ToPairs(items))
        {
            if (p.Source.Length == 0 && p.Target.Length == 0) continue;
            if (Touches(p.Source) || Touches(p.Target)) drop.Add(p); else keep.Add(p);
        }
        return (keep, drop);
    }

    /// <summary>"\??\C:\x" (the form Windows stores) to "C:\x"; "!\??\" marks a target to overwrite.</summary>
    public static string StripPrefix(string path)
    {
        string p = path.TrimStart('!');
        return p.StartsWith(@"\??\", StringComparison.Ordinal) ? p[4..] : p;
    }
}
