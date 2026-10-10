using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>Result of checking the service account against Windows.</summary>
internal enum CredentialCheck { Ok, Bad, Unknown }

/// <summary>The few Windows calls .NET has no managed API for.</summary>

internal static partial class NativeMethods
{
    // ── service account check ────────────────────────────────────────────────

    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string user, string? domain, string password, int logonType, int provider, out IntPtr token);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Checks account and password before the service is created; a typo would otherwise only
    /// show up later as a service that will not start (error 1069). A NETWORK logon validates the
    /// credentials without needing "Log on as a service", which the service installer grants
    /// afterwards. DOMAIN\user, user@domain (UPN) and a bare local name are accepted.
    /// </summary>
    public static CredentialCheck CheckServiceCredentials(string account, string password, out string message)
    {
        string? domain;
        string user;
        int sep = account.IndexOf('\\');
        if (sep > 0) { domain = account[..sep]; user = account[(sep + 1)..]; }
        else if (account.Contains('@')) { domain = null; user = account; }
        else { domain = "."; user = account; }

        if (LogonUser(user, domain, password, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out var token))
        {
            CloseHandle(token);
            message = "";
            return CredentialCheck.Ok;
        }

        int err = Marshal.GetLastPInvokeError();
        (CredentialCheck result, message) = err switch
        {
            1326 => (CredentialCheck.Bad, "The user name or password is wrong. Check the account (DOMAIN\\user) and re-enter the password."),
            1317 => (CredentialCheck.Bad, "That account does not exist. Check the spelling and the domain."),
            1331 => (CredentialCheck.Bad, "That account is disabled."),
            1909 => (CredentialCheck.Bad, "That account is locked out."),
            1330 or 1907 => (CredentialCheck.Bad, "The password for that account has expired and must be changed before it can be used for the service."),
            1327 => (CredentialCheck.Bad, "The credentials are correct, but the account is restricted (blank passwords are not allowed for service accounts)."),
            1328 => (CredentialCheck.Bad, "That account is not allowed to sign in at this time of day."),
            1329 => (CredentialCheck.Bad, "That account is not allowed to sign in on this computer."),
            1385 => (CredentialCheck.Ok, ""),   // credentials are fine; only this logon type is blocked by policy
            _ => (CredentialCheck.Unknown,
                  $"The credentials could not be verified on this computer ({new Win32Exception(err).Message}, error {err}). " +
                  "This usually means the domain controller is unreachable, not that the password is wrong."),
        };
        return result;
    }

    // ── raw REG_MULTI_SZ (see Logic.PendingRenames) ─────────────────────────

    private static readonly UIntPtr HKEY_LOCAL_MACHINE = unchecked((UIntPtr)0x80000002u);
    private const int KEY_QUERY_VALUE = 0x0001, KEY_SET_VALUE = 0x0002, KEY_WOW64_64KEY = 0x0100;
    private const int REG_MULTI_SZ = 7;
    private const int ERROR_FILE_NOT_FOUND = 2;

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyEx(UIntPtr hKey, string subKey, int options, int samDesired, out UIntPtr result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueEx(UIntPtr hKey, string valueName, IntPtr reserved, out int type, byte[]? data, ref int size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegSetValueEx(UIntPtr hKey, string valueName, int reserved, int type, byte[] data, int size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteValue(UIntPtr hKey, string valueName);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(UIntPtr hKey);

    /// <summary>The raw bytes of a REG_MULTI_SZ value under HKLM, or null when it does not exist.</summary>
    public static byte[]? ReadMultiStringRaw(string subKey, string valueName)
    {
        int rc = RegOpenKeyEx(HKEY_LOCAL_MACHINE, subKey, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, out var key);
        if (rc != 0) throw new Win32Exception(rc, $"Cannot open HKLM\\{subKey}.");
        try
        {
            int size = 0;
            rc = RegQueryValueEx(key, valueName, IntPtr.Zero, out int type, null, ref size);
            if (rc == ERROR_FILE_NOT_FOUND) return null;
            if (rc != 0) throw new Win32Exception(rc, $"Cannot read {valueName}.");
            if (type != REG_MULTI_SZ) return null;
            var data = new byte[size];
            rc = RegQueryValueEx(key, valueName, IntPtr.Zero, out _, data, ref size);
            if (rc != 0) throw new Win32Exception(rc, $"Cannot read {valueName}.");
            return data;
        }
        finally { _ = RegCloseKey(key); }
    }

    /// <summary>Write raw REG_MULTI_SZ bytes, or delete the value when <paramref name="data"/> is null.</summary>
    public static void WriteMultiStringRaw(string subKey, string valueName, byte[]? data)
    {
        int rc = RegOpenKeyEx(HKEY_LOCAL_MACHINE, subKey, 0, KEY_SET_VALUE | KEY_WOW64_64KEY, out var key);
        if (rc != 0) throw new Win32Exception(rc, $"Cannot open HKLM\\{subKey} for writing.");
        try
        {
            rc = data is null ? RegDeleteValue(key, valueName) : RegSetValueEx(key, valueName, 0, REG_MULTI_SZ, data, data.Length);
            if (rc != 0 && !(data is null && rc == ERROR_FILE_NOT_FOUND))
                throw new Win32Exception(rc, $"Cannot write {valueName}.");
        }
        finally { _ = RegCloseKey(key); }
    }

    public static RegistryKey LocalMachine64() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    // ── shortcuts (IShellLinkW) ──────────────────────────────────────────────

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, int flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    public static void CreateShortcut(string linkPath, string target, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }
}
