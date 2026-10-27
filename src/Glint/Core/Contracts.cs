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
}
