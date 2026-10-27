using System.Globalization;
using System.Runtime.InteropServices;
using Glint.Core;

namespace Glint.Services.Indicators;

/// <summary>
/// Caps Lock and keyboard layout flashes. A low-level keyboard hook only looks at the virtual-key code of
/// Caps Lock, modifiers and Space and never sees or stores what is typed; nothing polls in between.
/// The hook runs on its own thread with its own message loop: Windows calls it through that loop for every
/// keystroke in the system, so a busy UI thread would make all typing wait, and a hook that keeps Windows
/// waiting too long is silently removed.
/// </summary>
public sealed class KeyboardMonitor : IndicatorMonitor
{
    HookThread? _thread;

    public KeyboardMonitor(IndicatorHub hub) : base(hub) { }

    protected override void Start() => _thread = HookThread.Start();

    protected override void Stop()
    {
        _thread?.Quit();
        _thread = null;
    }

    /// <summary>"RU", "Русский" from a keyboard layout handle (its low word is the language id).</summary>
    internal static (string Code, string Name) Describe(IntPtr layout)
    {
        var langId = (int)((long)layout & 0xFFFF);
        try
        {
            var culture = CultureInfo.GetCultureInfo(langId);
            var name = culture.Parent is { Name.Length: > 0 } parent ? parent.NativeName : culture.NativeName;
            if (name.Length > 0) name = char.ToUpper(name[0], culture) + name[1..];
            return (culture.TwoLetterISOLanguageName.ToUpperInvariant(), name);
        }
        catch (CultureNotFoundException)
        {
            return ($"{langId:X4}", "Раскладка");
        }
    }

    /// <summary>One hook with its thread; all of its state is touched only on that thread.</summary>
    sealed class HookThread
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        const int WM_TIMER = 0x0113, WM_QUIT = 0x0012, WM_USER = 0x0400;
        const uint PM_NOREMOVE = 0;
        const int VK_CAPITAL = 0x14, VK_SPACE = 0x20;
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public int x, y;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool KillTimer(IntPtr hwnd, IntPtr id);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool on);
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

        // Held in a field: the hook calls through this delegate, and a collected one would crash the process.
        readonly HookProc _proc;
        readonly ManualResetEventSlim _ready = new();
        volatile uint _threadId;
        volatile bool _quit;
        IntPtr _hook;
        IntPtr _capsTimer, _layoutTimer;
        bool _capsOn, _capsHeld;

        // The layout before a modifier combo started, and the window it belonged to.
        bool _baselineFresh;
        IntPtr _baselineWindow;
        IntPtr _baselineLayout;

        HookThread() => _proc = HookCallback;

        static IndicatorSettings S => AppServices.Settings.Current.Indicators;

        public static HookThread Start()
        {
            var hook = new HookThread();
            new Thread(hook.Run) { IsBackground = true, Name = "Glint keyboard hook" }.Start();
            // Quit posts to the thread's queue, which exists only once the thread has started.
            hook._ready.Wait(TimeSpan.FromSeconds(2));
            return hook;
        }

        public void Quit()
        {
            // Either the thread sees the flag before its loop, or its queue already exists to take WM_QUIT.
            _quit = true;
            Thread.MemoryBarrier();
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        void Run()
        {
            try
            {
                // Peeking creates the message queue, so PostThreadMessage works from here on.
                PeekMessage(out _, IntPtr.Zero, WM_USER, WM_USER, PM_NOREMOVE);
                _threadId = GetCurrentThreadId();
                // The hook only reports changes, so read where Caps Lock stands now.
                _capsOn = ReadCaps() ?? (GetKeyState(VK_CAPITAL) & 1) != 0;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero)
                {
                    App.LogError(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Keyboard hook"));
                    return;
                }
                _ready.Set();
                if (_quit) return;
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_TIMER) OnTimer(msg.wParam);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                _ready.Set();
            }
        }

        IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            // Windows drops a hook that keeps it waiting: only a few cheap user32 calls here, the rest is deferred.
            if (code >= 0)
            {
                try
                {
                    var msg = (int)wParam;
                    var vk = Marshal.ReadInt32(lParam);
                    var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                    var up = msg is WM_KEYUP or WM_SYSKEYUP;
                    if (vk == VK_CAPITAL) OnCaps(down, up);
                    else if (IsModifier(vk) || vk == VK_SPACE) OnLayoutKey(vk, down, up);
                }
                catch (Exception)
                {
                    // Never let an exception unwind into the system's hook chain.
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        void OnTimer(IntPtr id)
        {
            if (id == _capsTimer)
            {
                KillTimer(IntPtr.Zero, _capsTimer);
                _capsTimer = IntPtr.Zero;
                FlashCaps();
            }
            else if (id == _layoutTimer)
            {
                KillTimer(IntPtr.Zero, _layoutTimer);
                _layoutTimer = IntPtr.Zero;
                CheckLayout();
            }
        }

        // A thread timer with no window: passing the running timer's id restarts it instead of adding another.
        static IntPtr Restart(IntPtr timer, uint ms) => SetTimer(IntPtr.Zero, timer, ms, IntPtr.Zero);

        void OnCaps(bool down, bool up)
        {
            if (up)
            {
                // The toggle has landed by the time the key is released; read it a moment later.
                if (_capsHeld && S.CapsLock) _capsTimer = Restart(_capsTimer, 50);
                _capsHeld = false;
                return;
            }
            // Holding the key repeats keydown; only the first press toggles.
            if (!down || _capsHeld) return;
            _capsHeld = true;
            _capsOn = !_capsOn;
        }

        void FlashCaps()
        {
            // Presses on the lock screen, over elevated windows or "Shift turns Caps Lock off" never reach the hook,
            // so read the real state instead of trusting the count; the count is only the fallback.
            if (ReadCaps() is bool real) _capsOn = real;
            var on = _capsOn;
            AppServices.Notifier.Flash(on ? "Caps Lock вкл" : "Caps Lock выкл", null, Glyphs.Keyboard, TimeSpan.FromSeconds(1.2));
        }

        /// <summary>
        /// Caps Lock as the foreground app sees it. Key state belongs to an input queue, and this thread's own
        /// never gets keyboard input, so share the foreground thread's for the moment of the read.
        /// Null when Windows refuses (an elevated window in front).
        /// </summary>
        bool? ReadCaps()
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;
            var target = Native.GetWindowThreadProcessId(fg, out _);
            if (target == 0 || target == _threadId || !AttachThreadInput(_threadId, target, true)) return null;
            try
            {
                return (GetKeyState(VK_CAPITAL) & 1) != 0;
            }
            finally
            {
                AttachThreadInput(_threadId, target, false);
            }
        }

        static bool IsModifier(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LSHIFT or VK_RSHIFT
            or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU or VK_LWIN or VK_RWIN;

        void OnLayoutKey(int vk, bool down, bool up)
        {
            if (!S.KeyboardLayout) return;
            if (down && IsModifier(vk) && !_baselineFresh)
            {
                // Remember the layout the combo starts from (Alt+Shift, Ctrl+Shift, Win+Space).
                var (window, layout) = ForegroundLayout();
                _baselineWindow = window;
                _baselineLayout = layout;
                _baselineFresh = layout != IntPtr.Zero;
            }
            else if (up && _baselineFresh)
            {
                // The switch lands when the combo is released; look shortly after.
                _layoutTimer = Restart(_layoutTimer, 200);
            }
        }

        void CheckLayout()
        {
            if (!_baselineFresh) return;
            var (window, layout) = ForegroundLayout();
            // Win+Space switches only when Win goes up; keep the baseline while any modifier is still held.
            var held = new[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);
            if (!held) _baselineFresh = false;
            // A different window (Alt+Tab) has its own layout; that isn't a switch.
            if (window != _baselineWindow || layout == IntPtr.Zero || layout == _baselineLayout) return;
            _baselineLayout = layout;
            var (code, name) = Describe(layout);
            AppServices.Notifier.Flash(code, name, Glyphs.Keyboard, TimeSpan.FromSeconds(1.2));
        }

        static (IntPtr Window, IntPtr Layout) ForegroundLayout()
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return (IntPtr.Zero, IntPtr.Zero);
            var thread = Native.GetWindowThreadProcessId(fg, out _);
            return (fg, GetKeyboardLayout(thread));
        }
    }
}
