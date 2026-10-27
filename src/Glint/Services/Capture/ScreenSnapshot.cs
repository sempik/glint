using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glint.Core;

namespace Glint.Services.Capture;

/// <summary>
/// The whole virtual screen frozen at one moment, in physical pixels. The selection overlay, the eyedropper,
/// QR and text recognition all work on it, so what the user sees while choosing is exactly what gets used.
/// Also remembers where windows and monitors were, so a plain click can pick one.
/// Rectangles are relative to the snapshot's top-left corner, not to the primary monitor.
/// </summary>
public sealed class ScreenSnapshot
{
    BitmapSource? _dimmed;

    /// <summary>Virtual-screen position of the snapshot's top-left pixel.</summary>
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>BGRA, stride = Width * 4, alpha always 255.</summary>
    public byte[] Pixels { get; }
    /// <summary>Frozen, 96 DPI, so one image unit is one pixel.</summary>
    public BitmapSource Image { get; }
    /// <summary>Visible top-level windows, topmost first.</summary>
    public IReadOnlyList<Int32Rect> Windows { get; }
    public IReadOnlyList<Int32Rect> Monitors { get; }

    ScreenSnapshot(int left, int top, int width, int height, byte[] pixels, IReadOnlyList<Int32Rect> windows, IReadOnlyList<Int32Rect> monitors)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        Pixels = pixels;
        Windows = windows;
        Monitors = monitors;
        Image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        Image.Freeze();
    }

    /// <summary>Copies the screen as it is now. Any thread; takes a few tens of milliseconds on a 4K screen.</summary>
    public static ScreenSnapshot Capture()
    {
        // The process is per-monitor DPI aware, so these are physical pixels across all monitors.
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var w = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var h = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var pixels = new byte[w * h * 4];
        using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                for (var row = 0; row < h; row++)
                    Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * w * 4, w * 4);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        // GDI leaves the fourth byte undefined; QR and OCR read it as transparency.
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

        var bounds = new Int32Rect(x, y, w, h);
        return new ScreenSnapshot(x, y, w, h, pixels, ListWindows(bounds), ListMonitors(bounds));
    }

    /// <summary>The same picture darkened, for the area around a selection. Built once, on first use.</summary>
    public BitmapSource Dimmed
    {
        get
        {
            if (_dimmed is not null) return _dimmed;
            var lut = new byte[256];
            for (var i = 0; i < 256; i++) lut[i] = (byte)(i * 0.45);
            var dark = new byte[Pixels.Length];
            for (var i = 0; i < dark.Length; i++) dark[i] = lut[Pixels[i]];
            var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, dark, Width * 4);
            image.Freeze();
            _dimmed = image;
            return image;
        }
    }

    public Int32Rect Bounds => new(0, 0, Width, Height);

    /// <summary>The rectangle limited to the snapshot; empty when they don't overlap.</summary>
    public Int32Rect Clip(Int32Rect r)
    {
        var x1 = Math.Clamp(r.X, 0, Width);
        var y1 = Math.Clamp(r.Y, 0, Height);
        var x2 = Math.Clamp(r.X + r.Width, 0, Width);
        var y2 = Math.Clamp(r.Y + r.Height, 0, Height);
        return new Int32Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    /// <summary>A frozen copy of part of the snapshot.</summary>
    public BitmapSource Crop(Int32Rect r)
    {
        r = Clip(r);
        var image = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null, CropPixels(r), r.Width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>BGRA bytes of part of the snapshot, stride = width * 4.</summary>
    public byte[] CropPixels(Int32Rect r)
    {
        r = Clip(r);
        var result = new byte[r.Width * r.Height * 4];
        for (var row = 0; row < r.Height; row++)
            Buffer.BlockCopy(Pixels, ((r.Y + row) * Width + r.X) * 4, result, row * r.Width * 4, r.Width * 4);
        return result;
    }

    public Color PixelAt(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var i = (y * Width + x) * 4;
        return Color.FromRgb(Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }

    /// <summary>Monitor containing the point, else the nearest one.</summary>
    public Int32Rect MonitorAt(int x, int y)
    {
        if (Monitors.Count == 0) return Bounds;
        foreach (var m in Monitors)
            if (Contains(m, x, y)) return m;
        return Monitors.MinBy(m => Distance(m, x, y));
    }

    public static bool Contains(Int32Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    static double Distance(Int32Rect r, int x, int y)
    {
        var dx = Math.Max(0, Math.Max(r.X - x, x - (r.X + r.Width)));
        var dy = Math.Max(0, Math.Max(r.Y - y, y - (r.Y + r.Height)));
        return dx * dx + dy * dy;
    }

    // ───────────── Windows and monitors ─────────────

    static List<Int32Rect> ListMonitors(Int32Rect bounds)
    {
        var list = new List<Int32Rect>();
        try
        {
            // Per-monitor aware, so WinForms reports physical pixels.
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var b = screen.Bounds;
                list.Add(new Int32Rect(b.X - bounds.X, b.Y - bounds.Y, b.Width, b.Height));
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return list;
    }

    /// <summary>Visible, uncloaked top-level windows of other apps in z-order, without the desktop itself.</summary>
    static List<Int32Rect> ListWindows(Int32Rect bounds)
    {
        var list = new List<Int32Rect>();
        var ownPid = (uint)Environment.ProcessId;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (list.Count >= 300) return false;
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == ownPid) return true;
                var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                // Click-through overlays (game bars, our own pill elsewhere) aren't something to pick.
                if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var cls = Native.GetClassName(hwnd);
                if (cls is "Progman" or "WorkerW") return true;
                // The extended frame leaves out the invisible resize borders Windows 10+ adds around windows.
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) != 0
                    && !Native.GetWindowRect(hwnd, out r)) return true;
                if (r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;
                list.Add(new Int32Rect(r.Left - bounds.X, r.Top - bounds.Y, r.Right - r.Left, r.Bottom - r.Top));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return list;
    }

    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Native.RECT value, int size);
}
