using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Glint.Core;
using Glint.Pill;
using Glint.Services.Capture;
using Glint.Services.Indicators;

namespace Glint.Capture;

/// <summary>
/// Entry points for the screen tools, shared by Главная, the command bar and the tray: screenshot with markup
/// (area, window or whole screen), QR and text from the screen, the eyedropper, and screen recording.
/// Each tool freezes the screen first (without Glint's own pill and panel), lets the user pick an area on that
/// frozen picture, then works on the pixels it already has. Everything is local. UI thread only.
/// </summary>
public static class ScreenTools
{
    const string RecordingIndicator = "capture.rec";

    static bool s_busy;
    static ScreenRecorder? s_recorder;
    static bool s_starting;

    public static string PicturesFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Glint");
    public static string VideosFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Glint");

    /// <summary>Raised on the UI thread when a screen recording starts or stops.</summary>
    public static event EventHandler? RecordingChanged;

    public static bool IsRecording => s_recorder is not null;

    /// <summary>When the running recording started; null when not recording.</summary>
    public static DateTime? RecordingSince => s_recorder?.StartedAt;

    static CaptureSettings Settings => AppServices.Settings.Current.Capture;

    static PillWindow? Pill => AppServices.Notifier as PillWindow;

    // ───────────── Tools ─────────────

    /// <summary>Area, window (click) or monitor (Enter), then the markup editor (or straight to clipboard and file).</summary>
    public static void CaptureArea() => Run(async () =>
    {
        var shot = await TakeSnapshotAsync();
        var rect = await ScreenOverlayWindow.PickAsync(shot, OverlayMode.Window, "Выделите область или нажмите на окно · Enter — весь экран · Esc — отмена");
        if (rect is not { } r) return;
        await DeliverAsync(shot.Crop(r), shot, r);
    });

    /// <summary>All monitors at once, straight into the editor.</summary>
    public static void CaptureFullScreen() => Run(async () =>
    {
        var shot = await TakeSnapshotAsync(forPicker: false);
        await DeliverAsync(shot.Image, shot, shot.Bounds);
    });

    public static void ScanQr() => Run(async () =>
    {
        var shot = await TakeSnapshotAsync();
        var rect = await ScreenOverlayWindow.PickAsync(shot, OverlayMode.Window, "Выделите QR-код или нажмите на окно с ним · Esc — отмена");
        if (rect is not { } r) return;
        var pixels = shot.CropPixels(r);
        var codes = await Task.Run(() => QrReader.Decode(pixels, r.Width, r.Height));
        if (codes.Count == 0)
        {
            AppServices.Notifier.Flash("Код не найден", "Выделите его плотнее или крупнее", Glyphs.QrCode, TimeSpan.FromSeconds(3));
            return;
        }
        ShowQr(codes);
    });

    public static void ReadText() => Run(async () =>
    {
        if (!TextRecognizer.IsAvailable)
        {
            ShowNoOcrLanguage();
            return;
        }
        var shot = await TakeSnapshotAsync();
        var rect = await ScreenOverlayWindow.PickAsync(shot, OverlayMode.Window, "Выделите текст или нажмите на окно · Esc — отмена");
        if (rect is not { } r) return;
        RecognizedText? result;
        try
        {
            result = await TextRecognizer.RecognizeAsync(shot.CropPixels(r), r.Width, r.Height);
        }
        catch (NoOcrLanguageException)
        {
            ShowNoOcrLanguage();
            return;
        }
        if (result is null)
        {
            AppServices.Notifier.Flash("Текст не найден", "Попробуйте выделить крупнее", Glyphs.ScanText, TimeSpan.FromSeconds(3));
            return;
        }
        var copied = CopyText(result.Text);
        CaptureResultWindow.Open(
            "Текст с экрана",
            (copied ? "Скопирован · " : "") + $"распознано: {result.LanguageName}",
            Glyphs.ScanText,
            result.Text,
            new[] { new ResultAction("Копировать", Glyphs.Copy, t => CopyWithFlash(t), Primary: true) });
    });

    public static void PickColor() => Run(async () =>
    {
        if (EyedropperWindow.IsOpen) return;
        var shot = await TakeSnapshotAsync(forPicker: false);
        EyedropperWindow.Open(shot);
    });

    /// <summary>Starts a recording (after choosing the monitor or an area), or stops the running one.</summary>
    public static void ToggleRecording()
    {
        if (IsRecording) _ = StopRecordingAsync(null);
        else StartRecording();
    }

    /// <summary>One picker at a time; errors end up in the log and a short flash.</summary>
    static async void Run(Func<Task> tool)
    {
        if (s_busy) return;
        s_busy = true;
        try
        {
            await tool();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier.Flash("Не получилось", ex.Message, Glyphs.Info, TimeSpan.FromSeconds(3));
        }
        finally
        {
            s_busy = false;
            ReleaseSnapshotSoon();
        }
    }

    /// <summary>
    /// A snapshot of a 4K screen is 30-70 MB in the large object heap, which a resting app may not collect for
    /// hours. Once the tool is done with it, give it back.
    /// </summary>
    internal static void ReleaseSnapshotSoon() =>
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }, TaskScheduler.Default);

    /// <summary>
    /// Freezes the screen. Whatever opened the tool (command bar, menu) gets a moment to disappear, and the pill
    /// and an open panel are kept out of the picture (always for the panel, for the pill as set in Настройки).
    /// </summary>
    static async Task<ScreenSnapshot> TakeSnapshotAsync(bool forPicker = true)
    {
        await Task.Delay(140);
        var pill = Pill;
        var exclude = pill is not null && (Settings.HideGlint || pill.IsExpanded);
        if (exclude)
        {
            pill!.ExcludeFromCapture(true);
            // The compositor applies it with its next frame.
            await Task.Delay(50);
        }
        try
        {
            return await Task.Run(() =>
            {
                var shot = ScreenSnapshot.Capture();
                // The area picker shows the darkened copy; build it here rather than on the UI thread.
                if (forPicker) _ = shot.Dimmed;
                return shot;
            });
        }
        finally
        {
            if (exclude) pill!.ExcludeFromCapture(false);
        }
    }

    /// <summary>The editor, or (with the editor turned off) clipboard + Pictures\Glint right away.</summary>
    static async Task DeliverAsync(BitmapSource image, ScreenSnapshot shot, Int32Rect area)
    {
        var center = (X: shot.Left + area.X + area.Width / 2, Y: shot.Top + area.Y + area.Height / 2);
        if (Settings.OpenEditor)
        {
            ScreenshotEditorWindow.Open(image, ScreenshotEditorWindow.MonitorScale(center.X, center.Y));
            return;
        }
        var png = await EncodePngAsync(image);
        var path = await SavePngAsync(png);
        CopyImage(image, png);
        FlashSaved(path);
    }

    // ───────────── QR ─────────────

    static void ShowQr(IReadOnlyList<ScannedCode> codes)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, codes.Select(c => c.Text));
        var first = codes[0];
        var actions = new List<ResultAction>();
        if (QrReader.AsWifi(first.Text) is { } wifi)
        {
            if (wifi.Password is { } password)
                actions.Add(new ResultAction("Копировать пароль", Glyphs.Lock, _ => CopyWithFlash(password, "Пароль Wi-Fi скопирован")));
            text = $"Сеть: {wifi.Ssid}" + (wifi.Password is null ? "" : $"{Environment.NewLine}Пароль: {wifi.Password}");
        }
        if (QrReader.AsLink(first.Text) is { } link)
            actions.Add(new ResultAction("Открыть ссылку", Glyphs.Link, _ => OpenLink(link)));
        actions.Add(new ResultAction("Копировать", Glyphs.Copy, t => CopyWithFlash(t), Primary: true));
        var subtitle = codes.Count == 1 ? first.Format : $"{first.Format} и ещё {codes.Count - 1}";
        CaptureResultWindow.Open(QrReader.AsWifi(first.Text) is not null ? "Wi-Fi из QR-кода" : "Код с экрана", subtitle, Glyphs.QrCode, text, actions);
    }

    static void OpenLink(Uri link)
    {
        try
        {
            Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier.Flash("Не удалось открыть", link.Host, Glyphs.Info);
        }
    }

    static void ShowNoOcrLanguage() =>
        AppServices.Notifier.FlashClickable(
            "Нет языка для распознавания",
            // The flash is 380 wide; a longer line gets cut off right where the call to action is.
            "Нажмите и добавьте русский или английский",
            Glyphs.ScanText,
            () => OpenLink(new Uri("ms-settings:regionlanguage")),
            TimeSpan.FromSeconds(6));

    // ───────────── Recording ─────────────

    /// <summary>Asks for the monitor or an area, then records it; nothing if a recording runs or is still being saved.</summary>
    public static async void StartRecording()
    {
        if (s_busy || s_starting || IsRecording) return;
        if (s_stopping is { IsCompleted: false })
        {
            AppServices.Notifier.Flash("Прошлая запись ещё сохраняется", "Попробуйте через пару секунд", Glyphs.Record, TimeSpan.FromSeconds(2.6));
            return;
        }
        if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
        {
            AppServices.Notifier.Flash("Запись экрана недоступна", "Нужна Windows 10 2004 или новее", Glyphs.Record, TimeSpan.FromSeconds(3));
            return;
        }
        s_busy = true;
        s_starting = true;
        var excluded = false;
        try
        {
            var shot = await TakeSnapshotAsync();
            var multi = shot.Monitors.Count > 1;
            var rect = await ScreenOverlayWindow.PickAsync(shot, OverlayMode.Monitor,
                (multi ? "Нажмите на экран для записи" : "Нажмите, чтобы записать весь экран,") + " или выделите область · Esc — отмена");
            if (rect is not { } r) return;

            // A capture covers one monitor; an area is limited to the monitor holding its centre.
            var monitor = shot.MonitorAt(r.X + r.Width / 2, r.Y + r.Height / 2);
            var area = shot.Clip(new Int32Rect(Math.Max(r.X, monitor.X), Math.Max(r.Y, monitor.Y),
                Math.Min(r.X + r.Width, monitor.X + monitor.Width) - Math.Max(r.X, monitor.X),
                Math.Min(r.Y + r.Height, monitor.Y + monitor.Height) - Math.Max(r.Y, monitor.Y)));
            Int32Rect? crop = area.Equals(monitor) ? null : new Int32Rect(area.X - monitor.X, area.Y - monitor.Y, area.Width, area.Height);
            var handle = MonitorFromPoint(new POINT { X = shot.Left + monitor.X + monitor.Width / 2, Y = shot.Top + monitor.Y + monitor.Height / 2 }, MONITOR_DEFAULTTONEAREST);

            if (Settings.HideGlint && Pill is { } pill)
            {
                // The pill keeps showing the timer on screen but stays out of the video.
                pill.ExcludeFromCapture(true);
                excluded = true;
            }
            var options = new RecordingOptions(Settings.RecordFrameRate, Settings.RecordSystemAudio, Settings.RecordCursor);
            var recorder = await ScreenRecorder.StartAsync(handle, crop, NewVideoPath(), options);
            recorder.Faulted += error => Application.Current?.Dispatcher.BeginInvoke(() => _ = StopRecordingAsync(error, recorder));
            s_recorder = recorder;
            s_excludedForRecording = excluded;
            excluded = false;
            ShowRecordingIndicator();
            RecordingChanged?.Invoke(null, EventArgs.Empty);
            var what = crop is null ? "весь экран" : $"область {area.Width} × {area.Height}";
            AppServices.Notifier.Flash("Запись началась", $"{what}{(recorder.HasAudio ? " со звуком" : "")} · стоп — красная точка", Glyphs.Record, TimeSpan.FromSeconds(2.6));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier.Flash("Не удалось начать запись", ex.Message, Glyphs.Info, TimeSpan.FromSeconds(4));
        }
        finally
        {
            if (excluded) Pill?.ExcludeFromCapture(false);
            s_starting = false;
            s_busy = false;
            ReleaseSnapshotSoon();
        }
    }

    // Whether the running recording holds one of the pill's capture exclusions.
    static bool s_excludedForRecording;
    // The file of the last stopped recording being finished; exit waits for it.
    static Task? s_stopping;

    static void ShowRecordingIndicator()
    {
        if (s_recorder is not { } r) return;
        // A start time stays true without ticking; the pill's own timer counts the seconds.
        IndicatorHub.Shared.Set(new Indicator(RecordingIndicator, Glyphs.Record, "DangerBrush", $"Запись экрана с {r.StartedAt:HH:mm}\nНажмите, чтобы остановить")
        {
            Label = $"с {r.StartedAt:HH:mm}",
            Click = () => _ = StopRecordingAsync(null),
            Order = -10,
            // The pill shows its own dot with a timer instead of the small badge.
            ChipOnly = true,
        });
    }

    /// <summary>Stops and finishes the file; a fault (monitor gone, encoder error) is reported, the file kept when possible.</summary>
    public static async Task StopRecordingAsync(Exception? fault, ScreenRecorder? which = null)
    {
        var recorder = s_recorder;
        if (recorder is null || (which is not null && !ReferenceEquals(which, recorder))) return;
        s_recorder = null;
        // Taken along now: a new recording may set its own before this one's file is finished.
        var excluded = s_excludedForRecording;
        s_excludedForRecording = false;
        IndicatorHub.Shared.Remove(RecordingIndicator);
        RecordingChanged?.Invoke(null, EventArgs.Empty);
        var length = DateTime.Now - recorder.StartedAt;
        try
        {
            // Off the UI thread, so exit can wait for it without a deadlock.
            var stopping = Task.Run(recorder.StopAsync);
            s_stopping = stopping;
            await stopping;
            var path = recorder.Path;
            if (fault is not null) App.LogError(fault);
            AppServices.Notifier.FlashClickable(
                fault is null ? "Запись сохранена" : "Запись прервалась, файл сохранён",
                $"{Path.GetFileName(path)} · {FormatLength(length)} · открыть",
                Glyphs.Record,
                () => OpenFile(path),
                TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            App.LogError(fault ?? ex);
            AppServices.Notifier.Flash("Не удалось сохранить запись", (fault ?? ex).Message, Glyphs.Info, TimeSpan.FromSeconds(4));
        }
        finally
        {
            if (excluded) Pill?.ExcludeFromCapture(false);
        }
    }

    /// <summary>On exit or logoff: finish the file so it stays playable, also one whose stop is still under way. Blocks up to a few seconds.</summary>
    public static void StopRecordingForExit()
    {
        var recorder = s_recorder;
        s_recorder = null;
        var stopping = recorder is null ? s_stopping : Task.Run(recorder.StopAsync);
        if (stopping is null) return;
        try
        {
            // The recorder never needs the UI thread to finish, so waiting here can't deadlock.
            stopping.Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    /// <summary>"0:42", "12:05", "1:02:33".</summary>
    public static string FormatLength(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    static void OpenFile(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            else IndicatorNative.ShowInExplorer(path);
        }
        catch (Exception ex)
        {
            // No player for .mp4: show the file instead.
            App.LogError(ex);
            IndicatorNative.ShowInExplorer(path);
        }
    }

    // ───────────── Files and clipboard ─────────────

    public static string NewScreenshotPath() => UniquePath(PicturesFolder, ".png");

    static string NewVideoPath() => UniquePath(VideosFolder, ".mp4");

    static string UniquePath(string dir, string extension)
    {
        var stem = $"Glint {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        var path = Path.Combine(dir, stem + extension);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i}){extension}");
        return path;
    }

    /// <summary>PNG of a frozen picture, encoded on a worker: a 4K frame takes a few hundred milliseconds.</summary>
    public static Task<byte[]> EncodePngAsync(BitmapSource image) => Task.Run(() =>
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    });

    /// <summary>Writes an encoded PNG into Pictures\Glint and returns its path.</summary>
    public static async Task<string> SavePngAsync(byte[] png)
    {
        Directory.CreateDirectory(PicturesFolder);
        var path = NewScreenshotPath();
        await File.WriteAllBytesAsync(path, png);
        return path;
    }

    public static void FlashSaved(string path) =>
        AppServices.Notifier.FlashClickable("Скриншот сохранён", Path.GetFileName(path) + " · показать", Glyphs.Camera, () => IndicatorNative.ShowInExplorer(path), TimeSpan.FromSeconds(4));

    /// <summary>
    /// Puts the picture on the clipboard both as a bitmap and as PNG (browsers, Discord and Telegram prefer PNG);
    /// <paramref name="png"/> is the same picture from <see cref="EncodePngAsync"/>. False if the clipboard stayed busy.
    /// </summary>
    public static bool CopyImage(BitmapSource image, byte[] png)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", new MemoryStream(png, writable: false));
        return TryClipboard(() => Clipboard.SetDataObject(data, true));
    }

    public static bool CopyText(string text) => TryClipboard(() => Clipboard.SetText(text));

    static void CopyWithFlash(string text, string title = "Скопировано")
    {
        if (CopyText(text)) AppServices.Notifier.Flash(title, FirstLine(text), Glyphs.Copy);
        else AppServices.Notifier.Flash("Буфер обмена занят", "Попробуйте ещё раз", Glyphs.Info);
    }

    static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    /// <summary>
    /// One attempt: WPF itself retries a busy clipboard for about a second, and more rounds on the UI thread
    /// would only freeze the pill for longer.
    /// </summary>
    static bool TryClipboard(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (COMException)
        {
            // Another app kept the clipboard open the whole time.
            return false;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return false;
        }
    }

    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
}
