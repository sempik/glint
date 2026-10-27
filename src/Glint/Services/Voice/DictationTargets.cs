using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glint.Core;

namespace Glint.Services.Voice;

/// <summary>
/// Dictation into one of Glint's own text boxes (the Текст editor, the command bar). Each phrase goes in at the
/// caret as soon as it's recognized, with a space before it when needed, through SelectedText so Ctrl+Z undoes it.
/// </summary>
public sealed class TextBoxDictationTarget : IDictationTarget
{
    readonly TextBox _box;
    readonly Action<string?>? _preview;
    readonly StringBuilder _lost = new();

    public TextBoxDictationTarget(TextBox box, string name, Action<string?>? preview = null)
    {
        _box = box;
        Name = name;
        _preview = preview;
    }

    public string Name { get; }

    public bool IsAlive => _box.IsVisible;

    /// <summary>True when the box lives in that window (the pill keeps its panel open meanwhile).</summary>
    public bool IsIn(Window window) => ReferenceEquals(Window.GetWindow(_box), window);

    public void ShowPartial(string? text) => _preview?.Invoke(text);

    public void AddPhrase(string text)
    {
        if (!IsAlive)
        {
            // The box went away mid-phrase; keep the words for the clipboard.
            Append(_lost, text);
            return;
        }
        var caret = _box.SelectionStart;
        var before = caret > 0 ? _box.Text[caret - 1] : '\0';
        var insert = before != '\0' && !char.IsWhiteSpace(before) && !StartsWithPunctuation(text) ? " " + text : text;
        _box.SelectedText = insert;
        _box.Select(caret + insert.Length, 0);
        _box.ScrollToEnd();
    }

    public void Finish()
    {
        _preview?.Invoke(null);
        if (_lost.Length == 0) return;
        var text = _lost.ToString();
        _lost.Clear();
        try
        {
            System.Windows.Clipboard.SetText(text);
            AppServices.Notifier.Flash("Продиктованное скопировано", Shorten(text), Glyphs.Microphone);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public bool FocusForVoiceTyping()
    {
        if (!IsAlive || Window.GetWindow(_box) is not { } window) return false;
        window.Activate();
        _box.Focus();
        Keyboard.Focus(_box);
        return _box.IsKeyboardFocused;
    }

    internal static void Append(StringBuilder sb, string text)
    {
        if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && !StartsWithPunctuation(text)) sb.Append(' ');
        sb.Append(text);
    }

    static bool StartsWithPunctuation(string text) => text.Length > 0 && char.IsPunctuation(text[0]) && text[0] is not '«' and not '"' and not '(';

    internal static string Shorten(string text) => text.Length > 60 ? text[..60].TrimEnd() + "…" : text;
}

/// <summary>
/// Dictation into another app's window (global hotkey, the command bar's "диктовка"). Phrases are collected while
/// the pill shows them live, and pasted once at the end the way clipboard history pastes, so focus is switched
/// and the clipboard touched only once; what was on the clipboard is put back afterwards.
/// </summary>
public sealed class WindowDictationTarget : IDictationTarget
{
    readonly IntPtr _hwnd;
    readonly StringBuilder _text = new();

    public WindowDictationTarget(IntPtr hwnd)
    {
        _hwnd = hwnd;
        var name = Native.GetProcessName(hwnd);
        Name = string.IsNullOrEmpty(name) ? "окно" : name;
    }

    public string Name { get; }

    public bool IsAlive => IsWindow(_hwnd);

    /// <summary>Everything recognized so far, shown in the pill's tooltip.</summary>
    public string Collected => _text.ToString();

    public void ShowPartial(string? text)
    {
        // The pill shows the live phrase; nothing to do in the other app until the end.
    }

    public void AddPhrase(string text) => TextBoxDictationTarget.Append(_text, text);

    public void Finish()
    {
        if (_text.Length == 0) return;
        var text = _text.ToString();
        _text.Clear();
        AppServices.Clipboard.PasteTextTo(IsAlive ? _hwnd : IntPtr.Zero, text);
    }

    public bool FocusForVoiceTyping()
    {
        if (!IsAlive) return false;
        if (Native.GetForegroundWindow() == _hwnd) return true;
        Native.SetForegroundWindow(_hwnd);
        return Native.GetForegroundWindow() == _hwnd;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(IntPtr hwnd);
}

/// <summary>
/// The fallback when Glint's recognizer can't run on this PC: Windows voice typing (Win+H), which ships with
/// Windows, has its own on-device and online models and its own first-run prompts, and types into the focused field.
/// </summary>
public static class WindowsVoiceTyping
{
    /// <summary>Focuses the target and presses Win+H. False if the focus couldn't be put there.</summary>
    public static bool TryOpen(IDictationTarget target)
    {
        try
        {
            if (!target.FocusForVoiceTyping()) return false;
            // Let the focus settle, like the clipboard paste does, before the shortcut arrives.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return false;
            _ = dispatcher.InvokeAsync(async () =>
            {
                // Started from a hotkey, its keys may still be down; Win+H under a held Ctrl+Alt is another shortcut.
                await Native.WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1.5));
                await Task.Delay(120);
                SendWinH();
            });
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return false;
        }
    }

    static void SendWinH()
    {
        var inputs = new[]
        {
            Key(VK_LWIN, up: false),
            Key(VK_H, up: false),
            Key(VK_H, up: true),
            Key(VK_LWIN, up: true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    const ushort VK_LWIN = 0x5B, VK_H = 0x48;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;

    static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, 0),
                // The Windows key is an extended key; without the flag some layouts read it as something else.
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (vk == VK_LWIN ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // MOUSEINPUT is the largest member, so it sets sizeof(INPUT) to the native 40 bytes on x64.
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
}
