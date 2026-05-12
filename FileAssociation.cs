using System;
using Microsoft.Win32;

namespace BeetleView;

/// <summary>
/// Registers a per-user file association in HKCU so the OS routes
/// .beetle double-clicks (and "Open With") to our .exe. No admin needed.
/// </summary>
internal static class FileAssociation
{
    public static void RegisterPerUser(string extension, string progId, string description, string exePath)
    {
        if (!extension.StartsWith('.')) throw new ArgumentException("extension must start with '.'", nameof(extension));

        // HKCU\Software\Classes\<progId>
        using (var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}", writable: true))
        {
            progKey.SetValue(null, description, RegistryValueKind.String);
            using var defaultIcon = progKey.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue(null, $"\"{exePath}\",0", RegistryValueKind.String);
            using var shell = progKey.CreateSubKey(@"shell\open\command");
            shell.SetValue(null, $"\"{exePath}\" \"%1\"", RegistryValueKind.String);
        }

        // HKCU\Software\Classes\<.ext> default value -> progId (primary handler when no UserChoice override)
        using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}", writable: true))
        {
            extKey.SetValue(null, progId, RegistryValueKind.String);
        }

        // Advertise as an "Open With" candidate so it appears in the picker even if another app owns UserChoice.
        using (var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true))
        {
            openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Nudge the shell to refresh its file-association cache.
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
