using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glint.Core;

namespace Glint.Pages;

/// <summary>Shell icons for shelf tiles. Called on the UI thread only.</summary>
internal static class ShelfIcons
{
    // Files of these types carry their own icon, so they can't share an extension-wide cache entry.
    static readonly HashSet<string> s_perFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".scr", ".appref-ms",
    };

    static readonly Dictionary<string, ImageSource?> s_cache = new(StringComparer.OrdinalIgnoreCase);
    static ShellInterop.IImageList? s_extraLarge;
    static bool s_imageListFailed;

    public static ImageSource? Get(string path)
    {
        var isFolder = Directory.Exists(path);
        string key;
        if (isFolder)
        {
            // Folders may have a custom desktop.ini icon.
            key = "dir:" + path;
        }
        else
        {
            var ext = Path.GetExtension(path);
            key = ext.Length == 0 || s_perFile.Contains(ext) ? "file:" + path : "ext:" + ext;
        }

        if (s_cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? icon = null;
        try
        {
            icon = Load(path, isFolder);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        s_cache[key] = icon;
        return icon;
    }

    static ImageSource? Load(string path, bool isFolder)
    {
        var size = (uint)Marshal.SizeOf<ShellInterop.SHFILEINFO>();
        var attrs = isFolder ? ShellInterop.FILE_ATTRIBUTE_DIRECTORY : ShellInterop.FILE_ATTRIBUTE_NORMAL;

        // SHGFI_LARGEICON is only 32px and looks soft in a 96px tile, so try the 48px system image list first.
        var info = new ShellInterop.SHFILEINFO();
        var found = ShellInterop.SHGetFileInfo(path, 0, ref info, size, ShellInterop.SHGFI_SYSICONINDEX) != IntPtr.Zero
                    || ShellInterop.SHGetFileInfo(path, attrs, ref info, size, ShellInterop.SHGFI_SYSICONINDEX | ShellInterop.SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero;
        if (found && ExtraLargeList() is { } list
            && list.GetIcon(info.iIcon, ShellInterop.ILD_TRANSPARENT, out var hIcon) == 0 && hIcon != IntPtr.Zero)
        {
            return FromHIcon(hIcon);
        }

        info = new ShellInterop.SHFILEINFO();
        const uint flags = ShellInterop.SHGFI_ICON | ShellInterop.SHGFI_LARGEICON;
        if ((ShellInterop.SHGetFileInfo(path, 0, ref info, size, flags) != IntPtr.Zero
             || ShellInterop.SHGetFileInfo(path, attrs, ref info, size, flags | ShellInterop.SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero)
            && info.hIcon != IntPtr.Zero)
        {
            return FromHIcon(info.hIcon);
        }
        return null;
    }

    static ShellInterop.IImageList? ExtraLargeList()
    {
        if (s_extraLarge is not null || s_imageListFailed) return s_extraLarge;
        try
        {
            var iid = typeof(ShellInterop.IImageList).GUID;
            if (ShellInterop.SHGetImageList(ShellInterop.SHIL_EXTRALARGE, ref iid, out var list) == 0)
                s_extraLarge = list;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        s_imageListFailed = s_extraLarge is null;
        return s_extraLarge;
    }

    static ImageSource? FromHIcon(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            ShellInterop.DestroyIcon(hIcon);
        }
    }

    static class ShellInterop
    {
        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_LARGEICON = 0x000000000;
        public const uint SHGFI_SYSICONINDEX = 0x000004000;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        public const int SHIL_EXTRALARGE = 2;
        public const int ILD_TRANSPARENT = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        // Only the slots up to GetIcon are declared; the vtable order must match commoncontrols.h.
        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll")]
        public static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}

/// <summary>ShelfItem → its shell icon, for the tile template.</summary>
public sealed class ShelfIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is ShelfItem item ? ShelfIcons.Get(item.Path) : null) ?? DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
