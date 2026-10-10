using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Native libraries (advapi32, kernel32) are loaded from System32 only.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: InternalsVisibleTo("Balsoft.Hive.Installer.Windows.Tests")]
