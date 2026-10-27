using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glint.Core;

namespace Glint.Services;

/// <summary>Clipboard history: listens for WM_CLIPBOARDUPDATE, stores text and images, pastes back into the previous window.</summary>
public sealed class ClipboardService : IClipboardService
{
    const string DataFile = "clipboard.json";
    const int MaxTextLength = 1_000_000;
    const long MaxImagePixels = 40_000_000;

    static readonly string ClipsDir = Path.Combine(SettingsStore.DataDir, "clips");

    // Password managers and other privacy-aware apps tag sensitive data with these formats.
    static readonly string[] PrivateFormats = { "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore" };
    const string HistoryFormat = "CanIncludeInClipboardHistory";

    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly object _saveLock = new();
    readonly SemaphoreSlim _imageGate = new(1, 1);
    readonly int _processId = Environment.ProcessId;
    readonly DateTime _startedAt = DateTime.Now;
    // Kept in a field: the native hook holds only a function pointer, so the delegate must outlive it.
    readonly Win32.WinEventProc _winEventProc;

    IntPtr _winEventHook;
    IntPtr _previousWindow;
    uint _lastSeenSequence;
    uint _ownSequence;
    bool _settingClipboard;
    byte[]? _lastImageHash;
    int _saveVersion;
    int _writtenVersion;

    public ObservableCollection<ClipEntry> Items { get; } = new();

    public ClipboardService()
    {
        _winEventProc = OnForegroundChanged;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(sync: false); };

        try
        {
            Load();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }

        if (Application.Current is { } app)
        {
            app.Exit += (_, _) =>
            {
                if (_winEventHook != IntPtr.Zero) Win32.UnhookWinEvent(_winEventHook);
                if (!_saveTimer.IsEnabled) return;
                _saveTimer.Stop();
                Save(sync: true);
            };
        }
        // A lower history limit is applied lazily on the next insert, so moving the Settings slider
        // down and back up again never deletes entries on its own.
    }

    public void Start(IntPtr hwnd)
    {
        try
        {
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            if (!Win32.AddClipboardFormatListener(hwnd))
                App.LogError(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            _lastSeenSequence = Win32.GetClipboardSequenceNumber();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }

        try
        {
            _winEventHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _winEventProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            RememberIfForeign(Native.GetForegroundWindow());
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ───────────── Public actions ─────────────

    public async void PasteToPreviousWindow(ClipEntry entry)
    {
        try
        {
            if (!await SetClipboardAsync(entry))
            {
                Flash("Не удалось скопировать", null);
                return;
            }
            MoveToTop(entry);

            var target = _previousWindow;
            if (target == IntPtr.Zero || !Win32.IsWindow(target))
            {
                Flash("Скопировано", Preview(entry));
                return;
            }

            FocusWindow(target);
            // Give the target a moment to process activation before it receives keystrokes.
            await Task.Delay(80);

            if (!SameProcess(Native.GetForegroundWindow(), target))
            {
                // Never type Ctrl+V into whatever happens to be focused instead.
                Flash("Скопировано", "Вставьте вручную: Ctrl+V");
                return;
            }
            SendCtrlV();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public async void CopyOnly(ClipEntry entry)
    {
        try
        {
            if (!await SetClipboardAsync(entry))
            {
                Flash("Не удалось скопировать", null);
                return;
            }
            MoveToTop(entry);
            Flash("Скопировано", Preview(entry));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public void TogglePin(ClipEntry entry)
    {
        entry.Pinned = !entry.Pinned;
        Reposition(entry);
        EnforceLimit();
        ScheduleSave();
    }

    public void Remove(ClipEntry entry)
    {
        RemoveCore(entry);
        ScheduleSave();
    }

    public void ClearUnpinned()
    {
        foreach (var entry in Items.Where(e => !e.Pinned).ToList())
            RemoveCore(entry);
        ScheduleSave();
    }

    // ───────────── Clipboard monitoring ─────────────

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE) _ = HandleClipboardUpdateAsync();
        return IntPtr.Zero;
    }

    async Task HandleClipboardUpdateAsync()
    {
        try
        {
            var sequence = Win32.GetClipboardSequenceNumber();
            if (_settingClipboard || sequence == _ownSequence || sequence == _lastSeenSequence) return;
            _lastSeenSequence = sequence;
            if (AppServices.Settings.Current.ClipboardHistoryLimit <= 0) return;

            var sourceApp = Native.GetProcessName(Native.GetForegroundWindow());
            // Our own tools (eyedropper, screenshot, text tools...) already show a more specific message.
            Native.GetWindowThreadProcessId(Win32.GetClipboardOwner(), out var ownerPid);
            var flash = ownerPid != _processId;
            var snapshot = await RetryAsync<Snapshot>(ReadSnapshot);
            if (snapshot is null) return;

            if (snapshot.Text is { } text)
                AddText(text, sourceApp, flash);
            else if (snapshot.Png is not null || snapshot.Pixels is not null)
                await AddImageAsync(snapshot.Png, snapshot.Pixels, sourceApp, flash);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    /// <summary>Must run on the UI (STA) thread; returns null for empty, unsupported or private content.</summary>
    static Snapshot? ReadSnapshot()
    {
        var data = System.Windows.Clipboard.GetDataObject();
        if (data is null) return null;

        foreach (var format in PrivateFormats)
            if (data.GetDataPresent(format, false)) return null;
        if (data.GetDataPresent(HistoryFormat, false)
            && data.GetData(HistoryFormat, false) is MemoryStream flag
            && flag.ToArray() is { Length: >= 4 } flagBytes
            && BitConverter.ToInt32(flagBytes, 0) == 0)
            return null;

        if (data.GetDataPresent(DataFormats.UnicodeText, true) && data.GetData(DataFormats.UnicodeText, true) is string text)
            return new Snapshot(text, null, null);

        // Browsers and modern apps publish a real PNG with alpha; prefer it over the lossy DIB route.
        if (data.GetDataPresent("PNG", false) && data.GetData("PNG", false) is MemoryStream png && png.Length > 0)
            return new Snapshot(null, png.ToArray(), null);

        if (data.GetDataPresent(DataFormats.Bitmap, true) && data.GetData(DataFormats.Bitmap, true) is BitmapSource bitmap)
            return ToPixels(bitmap) is { } pixels ? new Snapshot(null, null, pixels) : null;

        return null;
    }

    void AddText(string text, string sourceApp, bool flash)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return;

        var existing = Items.FirstOrDefault(e => e.Kind == ClipKind.Text && e.Text == text);
        if (existing is not null)
            MoveToTop(existing);
        else
            Insert(new ClipEntry { Kind = ClipKind.Text, Text = text, SourceApp = sourceApp });

        if (flash) Flash("Скопировано", FirstLine(text));
    }

    async Task AddImageAsync(byte[]? png, PixelBuffer? pixels, string sourceApp, bool flash)
    {
        var copiedAt = DateTime.Now;
        // One at a time, so a quick re-publish of the same image sees the hash stored by the previous call.
        await _imageGate.WaitAsync();
        try
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(ClipsDir, id + ".png");
            var lastHash = _lastImageHash;

            var (hash, written) = await Task.Run(() =>
            {
                var h = SHA256.HashData(pixels?.Pixels ?? png!);
                // Some apps re-publish the same image several times in a row.
                if (lastHash is not null && h.AsSpan().SequenceEqual(lastHash)) return (h, false);
                var bytes = png ?? EncodePng(pixels!);
                Directory.CreateDirectory(ClipsDir);
                File.WriteAllBytes(path, bytes);
                return (h, true);
            });

            _lastImageHash = hash;
            if (!written) return;

            Insert(new ClipEntry { Id = id, Time = copiedAt, Kind = ClipKind.Image, ImagePath = path, SourceApp = sourceApp });
            if (flash) Flash("Скопировано", "Изображение");
        }
        finally
        {
            _imageGate.Release();
        }
    }

    static PixelBuffer? ToPixels(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaxImagePixels) return null;

        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Bgr32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var buffer = new byte[stride * source.PixelHeight];
        source.CopyPixels(buffer, stride, 0);
        return new PixelBuffer(source.PixelWidth, source.PixelHeight, stride, source.Format, buffer);
    }

    static byte[] EncodePng(PixelBuffer p)
    {
        // 32-bit DIBs often carry an all-zero alpha channel that would make the PNG fully transparent.
        if (p.Format == PixelFormats.Bgra32 && AlphaIsEmpty(p.Pixels))
            for (var i = 3; i < p.Pixels.Length; i += 4) p.Pixels[i] = 0xFF;

        var bitmap = BitmapSource.Create(p.Width, p.Height, 96, 96, p.Format, null, p.Pixels, p.Stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    static bool AlphaIsEmpty(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return false;
        return true;
    }

    // ───────────── Writing to the clipboard ─────────────

    async Task<bool> SetClipboardAsync(ClipEntry entry)
    {
        var data = new DataObject();
        if (entry.Kind == ClipKind.Text)
        {
            if (string.IsNullOrEmpty(entry.Text)) return false;
            data.SetText(entry.Text, TextDataFormat.UnicodeText);
        }
        else
        {
            var imagePath = entry.ImagePath;
            if (imagePath is null || !File.Exists(imagePath)) return false;
            var bytes = await Task.Run(() => File.ReadAllBytes(imagePath));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            data.SetImage(image);
            data.SetData("PNG", new MemoryStream(bytes));
        }

        return await RetryAsync(() =>
        {
            _settingClipboard = true;
            try
            {
                System.Windows.Clipboard.SetDataObject(data, true);
            }
            finally
            {
                _settingClipboard = false;
            }
            // The WM_CLIPBOARDUPDATE for this change is still queued; this lets the handler recognise it as ours.
            _ownSequence = Win32.GetClipboardSequenceNumber();
            return true;
        });
    }

    /// <summary>The clipboard is a shared lock other apps hold briefly; retry instead of failing.</summary>
    static async Task<T?> RetryAsync<T>(Func<T?> action)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException)
            {
                // COMException (CLIPBRD_E_CANT_OPEN) derives from ExternalException.
            }
            if (attempt < 5) await Task.Delay(30);
        }
        return default;
    }

    // ───────────── Previous window + paste ─────────────

    void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            RememberIfForeign(hwnd);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void RememberIfForeign(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _processId) return;
        // Clicking the taskbar to reach Glint shouldn't make the taskbar the paste target.
        var cls = Native.GetClassName(hwnd);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return;
        _previousWindow = hwnd;
    }

    static void FocusWindow(IntPtr target)
    {
        if (Win32.IsIconic(target)) Win32.ShowWindow(target, Win32.SW_RESTORE);

        // Attaching to the current foreground thread lifts the foreground lock for SetForegroundWindow.
        var foreground = Native.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(foreground, out _);
        var ourThread = Win32.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ourThread && Win32.AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            Win32.BringWindowToTop(target);
            Native.SetForegroundWindow(target);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    static bool SameProcess(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
        if (a == b) return true;
        Native.GetWindowThreadProcessId(a, out var pa);
        Native.GetWindowThreadProcessId(b, out var pb);
        return pa != 0 && pa == pb;
    }

    static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(Win32.VK_CONTROL, up: false),
            Key(Win32.VK_V, up: false),
            Key(Win32.VK_V, up: true),
            Key(Win32.VK_CONTROL, up: true),
        };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    static Win32.INPUT Key(ushort vk, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)Win32.MapVirtualKey(vk, 0),
                dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0,
            },
        },
    };

    // ───────────── Collection upkeep ─────────────

    static bool Precedes(ClipEntry a, ClipEntry b) => a.Pinned != b.Pinned ? a.Pinned : a.Time >= b.Time;

    int IndexFor(ClipEntry entry)
    {
        for (var i = 0; i < Items.Count; i++)
            if (Precedes(entry, Items[i])) return i;
        return Items.Count;
    }

    void Insert(ClipEntry entry)
    {
        Items.Insert(IndexFor(entry), entry);
        EnforceLimit();
        ScheduleSave();
    }

    void MoveToTop(ClipEntry entry)
    {
        entry.Time = DateTime.Now;
        Reposition(entry);
        ScheduleSave();
    }

    // Remove + insert rather than Move so item containers are rebuilt and pick up Pinned/Time changes.
    void Reposition(ClipEntry entry)
    {
        if (!Items.Remove(entry)) return;
        Items.Insert(IndexFor(entry), entry);
    }

    /// <returns>True if anything was removed.</returns>
    bool EnforceLimit()
    {
        var limit = Math.Max(0, AppServices.Settings?.Current.ClipboardHistoryLimit ?? 200);
        var unpinned = Items.Where(e => !e.Pinned).ToList();
        if (unpinned.Count <= limit) return false;
        for (var i = limit; i < unpinned.Count; i++) RemoveCore(unpinned[i]);
        return true;
    }

    void RemoveCore(ClipEntry entry)
    {
        Items.Remove(entry);
        if (entry.ImagePath is { } path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }

    // ───────────── Persistence ─────────────

    void Load()
    {
        var saved = SettingsStore.ReadJson<List<ClipEntry>>(DataFile);
        if (saved is not null)
        {
            var valid = saved
                .Where(e => e.Kind == ClipKind.Text
                    ? !string.IsNullOrEmpty(e.Text)
                    : e.ImagePath is not null && File.Exists(e.ImagePath))
                .OrderByDescending(e => e.Pinned)
                .ThenByDescending(e => e.Time);
            foreach (var entry in valid) Items.Add(entry);
            EnforceLimit();
        }

        var keep = new HashSet<string>(
            Items.Where(e => e.ImagePath is not null).Select(e => Path.GetFullPath(e.ImagePath!)),
            StringComparer.OrdinalIgnoreCase);
        var startedAt = _startedAt;
        Task.Run(() =>
        {
            // Drop PNGs left behind by a crash or an edited json. Files newer than startup belong to this session.
            try
            {
                if (!Directory.Exists(ClipsDir)) return;
                foreach (var file in Directory.EnumerateFiles(ClipsDir, "*.png"))
                {
                    if (keep.Contains(Path.GetFullPath(file)) || File.GetLastWriteTime(file) >= startedAt) continue;
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        });
    }

    void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void Save(bool sync)
    {
        var snapshot = Items.ToList();
        var version = ++_saveVersion;

        void Write()
        {
            lock (_saveLock)
            {
                // A slower background write must not overwrite a newer snapshot.
                if (version < _writtenVersion) return;
                try
                {
                    SettingsStore.WriteJson(DataFile, snapshot);
                    _writtenVersion = version;
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
            }
        }

        if (sync) Write();
        else Task.Run(Write);
    }

    // ───────────── Helpers ─────────────

    static void Flash(string title, string? subtitle)
    {
        try
        {
            AppServices.Notifier?.Flash(title, subtitle, Glyphs.Clipboard, TimeSpan.FromSeconds(1.6));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static string Preview(ClipEntry entry) => entry.Kind == ClipKind.Image ? "Изображение" : FirstLine(entry.Text ?? string.Empty);

    static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length > 60 ? line[..60].TrimEnd() + "…" : line;
    }

    sealed record PixelBuffer(int Width, int Height, int Stride, PixelFormat Format, byte[] Pixels);

    sealed record Snapshot(string? Text, byte[]? Png, PixelBuffer? Pixels);

    static class Win32
    {
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_V = 0x56;
        public const int SW_RESTORE = 9;

        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // The union must include MOUSEINPUT (the largest member) so sizeof(INPUT) matches native (40 bytes on x64).
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    }
}
