using System;
using System.IO;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>packer &lt;payload folder&gt; &lt;output file&gt; &lt;version&gt;: writes the payload Setup embeds.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: packer <payload folder> <output file> <version>");
            return 2;
        }
        try
        {
            PayloadArchive.Create(args[0], args[1], args[2]);
            Console.WriteLine($"Installer payload {args[1]}: version {args[2]}, {new FileInfo(args[1]).Length / 1024.0 / 1024.0:0.0} MB.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }
}
