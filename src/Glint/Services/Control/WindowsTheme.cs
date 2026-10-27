using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Glint.Services.Control;

/// <summary>
/// Windows' dark/light mode: the two Personalize values the Settings app writes, plus the broadcast that makes
/// Explorer, the taskbar and open apps repaint without signing out.
/// </summary>
public static class WindowsTheme
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when apps use the light theme (the default when the value is missing).</summary>
    public static bool AppsUseLight => Read("AppsUseLightTheme") ?? true;

    public static bool SystemUsesLight => Read("SystemUsesLightTheme") ?? true;

    static bool? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is int v ? v != 0 : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Switches apps and the system (taskbar, Start) together, like the "Светлый/Тёмный" choice in Settings.</summary>
    public static void Set(bool light)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(Key))
        {
            key.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        }
        // A broadcast waits on every top-level window; a hung one would stall the caller, so don't block on it.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out IntPtr _);
        });
    }

    static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
