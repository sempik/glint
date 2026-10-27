using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Glint.Core;

/// <summary>Short-lived message shown inside the collapsed pill ("Copied", "Timer done", ...).</summary>
public interface IPillNotifier
{
    void Flash(string title, string? subtitle = null, string glyph = Glyphs.Info, TimeSpan? duration = null);

    /// <summary>
    /// A message that must not be lost (timer, reminder): queued while the panel is open or the pill is hidden,
    /// never expires and is never replaced by later flashes.
    /// </summary>
    void FlashImportant(string title, string? subtitle = null, string glyph = Glyphs.Info, TimeSpan? duration = null);

    /// <summary>
    /// A flash the user can click (e.g. "Загружено" opens the file). Hovering it doesn't open the panel
    /// and holds the countdown, so there is time to aim.
    /// </summary>
    void FlashClickable(string title, string? subtitle, string glyph, Action onClick, TimeSpan? duration = null);
}

public sealed record MediaInfo(
    string Title,
    string Artist,
    string SourceApp,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    ImageSource? Thumbnail);

/// <summary>Current system media session (Windows GSMTC).</summary>
public interface IMediaService
{
    MediaInfo? Current { get; }
    /// <summary>Raised on any thread when track, playback state or timeline changes.</summary>
    event EventHandler? Changed;
    Task InitializeAsync();
    Task PlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();
}

/// <summary>Default playback device master volume.</summary>
public interface IVolumeService
{
    /// <summary>0..1</summary>
    float Volume { get; set; }
    bool Muted { get; set; }
    /// <summary>Raised on any thread when volume or mute changes (including from outside the app).</summary>
    event EventHandler? Changed;
}

public enum ClipKind { Text, Image }

public sealed class ClipEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Time { get; set; } = DateTime.Now;
    public ClipKind Kind { get; init; }
    public string? Text { get; init; }
    /// <summary>PNG stored under the app data folder for image entries.</summary>
    public string? ImagePath { get; init; }
    public string? SourceApp { get; init; }
    public bool Pinned { get; set; }
}

public interface IClipboardService
{
    /// <summary>Newest first; pinned entries stay at the top. Mutated on the UI thread only.</summary>
    ObservableCollection<ClipEntry> Items { get; }
    /// <summary>Start listening for clipboard updates using the given window handle.</summary>
    void Start(IntPtr hwnd);
    /// <summary>Put the entry on the clipboard, focus the window the user was typing in before, and send Ctrl+V.</summary>
    void PasteToPreviousWindow(ClipEntry entry);
    /// <summary>
    /// Pastes plain text into a given window the same way (clipboard + Ctrl+V), without adding it to the history,
    /// then puts back what was on the clipboard before. Used by dictation.
    /// </summary>
    void PasteTextTo(IntPtr target, string text);
    /// <summary>The last foreign window that had the foreground (where history entries get pasted).</summary>
    IntPtr PreviousWindow { get; }
    void CopyOnly(ClipEntry entry);
    void TogglePin(ClipEntry entry);
    void Remove(ClipEntry entry);
    void ClearUnpinned();
}

public sealed class ShelfItem
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    public bool IsFolder => Directory.Exists(Path);
    public DateTime Added { get; init; } = DateTime.Now;
}

/// <summary>Files parked on the pill. Stores references, not copies.</summary>
public interface IShelfService
{
    ObservableCollection<ShelfItem> Items { get; }
    void Add(IEnumerable<string> paths);
    void Remove(ShelfItem item);
    void Clear();
}

/// <summary>Global access point for services, filled in by App on startup.</summary>
public static class AppServices
{
    public static SettingsStore Settings { get; set; } = null!;
    public static IPillNotifier Notifier { get; set; } = null!;
    public static IMediaService Media { get; set; } = null!;
    public static IVolumeService Volume { get; set; } = null!;
    public static IClipboardService Clipboard { get; set; } = null!;
    public static IShelfService Shelf { get; set; } = null!;
    /// <summary>Opens or closes the Alt+Space command bar.</summary>
    public static Action ToggleCommandBar { get; set; } = () => { };
    /// <summary>Windows notification (tray balloon) for messages the pill can't show right now.</summary>
    public static Action<string, string?>? SystemNotify { get; set; }
}

/// <summary>Segoe Fluent Icons code points (ship with Windows 11; Segoe MDL2 Assets fallback on Windows 10).</summary>
public static class Glyphs
{
    public const string Home = "";
    public const string Clipboard = "";
    public const string Shelf = "";
    public const string Text = "";
    public const string Settings = "";
    public const string Pin = "";
    public const string Unpin = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Next = "";
    public const string Previous = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Search = "";
    public const string Copy = "";
    public const string Delete = "";
    public const string Info = "";
    public const string Check = "";
    public const string Clock = "";
    public const string Camera = "";
    public const string Eyedropper = "";
    public const string QrCode = "";
    public const string Add = "";
    public const string Close = "";
    public const string Music = "";
    public const string Download = "";
    public const string Lightning = "";
    public const string Calculator = "";
    public const string Apps = "";
    public const string Document = "";
    public const string Folder = "";
    public const string Globe = "";
    public const string Lock = "";
    public const string Image = "";
    public const string Gauge = "\uE9D9";
    // Pill indicators
    public const string Microphone = "\uE720";
    public const string Webcam = "\uE714";
    public const string Keyboard = "\uE765";
    public const string Record = "\uE7C8";
    public const string Shield = "\uEA18";
    public const string Game = "\uE7FC";
    public const string Bell = "\uEA8F";
    // Пульт (control centre)
    public const string Sliders = "\uE9E9";
    public const string Wifi = "\uE701";
    public const string Bluetooth = "\uE702";
    public const string Headphones = "\uE7F6";
    public const string Speaker = "\uE7F5";
    public const string Vpn = "\uE705";
    public const string Sun = "\uE706";
    public const string Moon = "\uE708";
    // Notes, reminders, currency
    public const string Note = "\uE70B";
    public const string Currency = "\uE8C7";
    // Screen tools: screenshot editor, text from screen, recording
    public const string Pen = "\uE70F";
    public const string Highlighter = "\uE7E6";
    public const string Arrow = "\uE72A";
    public const string Rectangle = "\uE739";
    public const string Ellipse = "\uEA3A";
    public const string Font = "\uE8D2";
    public const string Redact = "\uED1A";
    public const string Crop = "\uE7A8";
    public const string Undo = "\uE7A7";
    public const string Redo = "\uE7A6";
    public const string Save = "\uE74E";
    public const string More = "\uE712";
    public const string Stop = "\uE71A";
    public const string ScanText = "\uE8C1";
    public const string Link = "\uE71B";
    public const string Monitor = "\uE7F4";
    // Weather and voice input
    public const string Cloud = "\uE753";
    public const string Refresh = "\uE72C";
    public const string MapPin = "\uE81D";
    // Coding agents (Claude Code, Codex)
    public const string Agent = "\uE99A";
    public const string Terminal = "\uE756";
    public const string Question = "\uE897";
}
