using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glint.Services.Indicators;

/// <summary>Small helpers shared by the indicator monitors.</summary>
internal static class IndicatorNative
{
    const uint FILE_READ_ATTRIBUTES = 0x80;
    const uint FILE_READ_DATA = 0x1;
    const uint FILE_SHARE_READ = 0x1, FILE_SHARE_DELETE = 0x4;
    const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    const int ERROR_SHARING_VIOLATION = 32;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileSizeEx(SafeFileHandle file, out long size);

    /// <summary>
    /// Size of a file another process is still writing. The directory entry (FileInfo.Length) lags behind for
    /// open files, so ask the file itself; attribute-only access passes any sharing mode the writer chose.
    /// </summary>
    public static long? TryGetLiveSize(string path)
    {
        try
        {
            using var handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (!handle.IsInvalid && GetFileSizeEx(handle, out var size)) return size;
        }
        catch (Exception)
        {
            // Fall through to the cached size.
        }
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True while another process holds the file open for writing (a paused recording, say). Opening it for
    /// reading while denying write sharing fails exactly then; the handle is closed again right away.
    /// </summary>
    public static bool IsOpenForWriting(string path)
    {
        try
        {
            using var handle = CreateFile(path, FILE_READ_DATA, FILE_SHARE_READ | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            return handle.IsInvalid && Marshal.GetLastWin32Error() == ERROR_SHARING_VIOLATION;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Opens Explorer with the file selected, a folder as is, or the parent folder if the file is gone.</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    /// <summary>"1,2 МБ", "640 КБ".</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.#} {units[unit]}";
    }
}
