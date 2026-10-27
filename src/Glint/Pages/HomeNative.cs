using System.Runtime.InteropServices;

namespace Glint.Pages;

/// <summary>Win32 imports used by the Home page (and the pill's screen width). Screen tools keep theirs in Capture/.</summary>
internal static class HomeNative
{
    static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool LockWorkStation();

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The user's Downloads folder, which may be relocated away from the profile.</summary>
    public static string GetDownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
