using System.Windows.Input;
using System.Windows.Interop;

namespace Glint.Core;

/// <summary>Registers system-wide hotkeys like "Alt+Space" or "Ctrl+Shift+V" on a window handle.</summary>
public sealed class HotkeyManager : IDisposable
{
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    readonly HwndSource _source;
    readonly Dictionary<int, Action> _handlers = new();
    int _nextId = 0x4700;

    public HotkeyManager(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("No HwndSource for window.");
        _source.AddHook(WndProc);
    }

    /// <summary>Returns the registration id, or null if the gesture is invalid or already taken by another app.</summary>
    public int? Register(string gesture, Action handler)
    {
        if (!TryParse(gesture, out var mods, out var vk)) return null;
        var id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk)) return null;
        _handlers[id] = handler;
        return id;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id)) Native.UnregisterHotKey(_source.Handle, id);
    }

    public static bool TryParse(string? gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= MOD_ALT; break;
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    Key key;
                    // Enum.TryParse accepts numbers ("1" would become Key.Cancel), so map digits explicitly.
                    if (raw.Length == 1 && char.IsAsciiDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (raw.All(char.IsAsciiDigit) || !Enum.TryParse(raw, ignoreCase: true, out key) || !Enum.IsDefined(key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var h))
        {
            h();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList()) Unregister(id);
        _source.RemoveHook(WndProc);
    }
}
