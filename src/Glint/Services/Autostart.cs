using Glint.Core;
using Microsoft.Win32;

namespace Glint.Services;

/// <summary>Per-user "start with Windows" via the HKCU Run key (no admin rights needed).</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Glint";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return false;
            }
        }
    }

    /// <summary>Writes or removes the Run entry and mirrors the result into settings (caller saves).</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }

        if (AppServices.Settings is { } store)
            store.Current.StartWithWindows = IsEnabled;
    }
}
