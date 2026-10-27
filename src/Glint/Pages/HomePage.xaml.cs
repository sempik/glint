using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glint.Core;

namespace Glint.Pages;

public partial class HomePage : UserControl
{
    readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    bool _attached;
    bool _syncingVolume;
    bool _capturing;
    MediaInfo? _lastMedia;
    DateTime _lastMediaAt;
    string? _dockSignature;

    public HomePage()
    {
        InitializeComponent();

        _progressTimer.Tick += (_, _) => RefreshMedia();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateTimerState();

        PrevButton.Click += async (_, _) => await RunMediaAsync(m => m.PreviousAsync());
        PlayPauseButton.Click += async (_, _) => await RunMediaAsync(m => m.PlayPauseAsync());
        NextButton.Click += async (_, _) => await RunMediaAsync(m => m.NextAsync());
        ProgressTrack.SizeChanged += (_, _) => RefreshMedia();

        VolumeSlider.ValueChanged += OnVolumeSliderChanged;
        MuteButton.Click += (_, _) => ToggleMute();
        VolumeRow.PreviewMouseWheel += OnVolumeWheel;

        RegionShotButton.Click += (_, _) => StartRegionShot();
        FullShotButton.Click += (_, _) => CaptureFullScreen();
        EyedropperButton.Click += (_, _) => EyedropperWindow.Start();
        CommandBarTile.Click += (_, _) => AppServices.ToggleCommandBar();
        LockButton.Click += (_, _) => Guard(() => HomeNative.LockWorkStation());
        DownloadsButton.Click += (_, _) => ShellOpen(HomeNative.GetDownloadsFolder());

        AddDockButton.Click += (_, _) => AddDockApps();
        DockCard.DragEnter += OnDockDragOver;
        DockCard.DragOver += OnDockDragOver;
        DockCard.Drop += OnDockDrop;
        DockScroll.PreviewMouseWheel += OnDockWheel;
    }

    // ───────────── Lifetime ─────────────

    // The pill caches pages and re-attaches them, so Loaded/Unloaded can fire many times.
    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        try
        {
            AppServices.Media.Changed += OnMediaChanged;
            AppServices.Volume.Changed += OnVolumeChanged;
            AppServices.Settings.Changed += OnSettingsChanged;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        RefreshMedia();
        SyncVolume();
        RebuildDock();
        UpdateTimerState();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_attached) return;
        _attached = false;
        try
        {
            AppServices.Media.Changed -= OnMediaChanged;
            AppServices.Volume.Changed -= OnVolumeChanged;
            AppServices.Settings.Changed -= OnSettingsChanged;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        UpdateTimerState();
    }

    void UpdateTimerState()
    {
        if (_attached && IsVisible)
        {
            RefreshMedia();
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }
    }

    void OnMediaChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RefreshMedia);
    void OnVolumeChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(SyncVolume);
    void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(RebuildDock);

    static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ───────────── Media ─────────────

    void RefreshMedia()
    {
        MediaInfo? media;
        try
        {
            media = AppServices.Media?.Current;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            media = null;
        }

        if (media is null)
        {
            _lastMedia = null;
            MediaView.Visibility = Visibility.Collapsed;
            MediaEmpty.Visibility = Visibility.Visible;
            return;
        }

        // Value equality: a new snapshot (track, state or timeline) restarts local extrapolation.
        // If the service already extrapolates, every snapshot differs and this adds nothing.
        if (media != _lastMedia)
        {
            _lastMedia = media;
            _lastMediaAt = DateTime.UtcNow;
            ApplyTrack(media);
        }
        UpdateProgress(media);
    }

    void ApplyTrack(MediaInfo media)
    {
        MediaView.Visibility = Visibility.Visible;
        MediaEmpty.Visibility = Visibility.Collapsed;

        TitleText.Text = string.IsNullOrWhiteSpace(media.Title) ? "Без названия" : media.Title;
        TitleText.ToolTip = TitleText.Text;
        ArtistText.Text = media.Artist;
        ArtistText.Visibility = string.IsNullOrWhiteSpace(media.Artist) ? Visibility.Collapsed : Visibility.Visible;
        var source = FriendlySource(media.SourceApp);
        SourceText.Text = source;
        SourceText.Visibility = source.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (!ReferenceEquals(ArtBrush.ImageSource, media.Thumbnail))
        {
            try
            {
                ArtBrush.ImageSource = media.Thumbnail;
            }
            catch (Exception ex)
            {
                // e.g. an unfrozen image created on another thread
                App.LogError(ex);
                ArtBrush.ImageSource = null;
            }
        }
        var hasArt = ArtBrush.ImageSource is not null;
        ArtImage.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
        ArtFallback.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;

        PlayPauseIcon.Text = media.IsPlaying ? Glyphs.Pause : Glyphs.Play;
        PlayPauseButton.ToolTip = media.IsPlaying ? "Пауза" : "Воспроизвести";
    }

    void UpdateProgress(MediaInfo media)
    {
        var duration = media.Duration;
        var position = media.Position;
        if (media.IsPlaying) position += DateTime.UtcNow - _lastMediaAt;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        if (duration <= TimeSpan.Zero)
        {
            // Live streams and some players report no timeline.
            ProgressFill.Width = 0;
            ElapsedText.Text = string.Empty;
            TotalText.Text = string.Empty;
            return;
        }

        if (position > duration) position = duration;
        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * (position.TotalSeconds / duration.TotalSeconds));
        ElapsedText.Text = FormatTime(position);
        TotalText.Text = FormatTime(duration);
    }

    static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>Turns "Spotify.exe" or a packaged app id into something readable.</summary>
    static string FriendlySource(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return string.Empty;
        var s = app.Trim();
        var bang = s.LastIndexOf('!');
        if (bang >= 0 && bang < s.Length - 1) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        var dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1 && !s.Contains(' ')) s = s[(dot + 1)..];
        return s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s;
    }

    static async Task RunMediaAsync(Func<IMediaService, Task> action)
    {
        try
        {
            if (AppServices.Media is { } media) await action(media);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ───────────── Volume ─────────────

    void SyncVolume()
    {
        float volume;
        bool muted;
        try
        {
            if (AppServices.Volume is null) return;
            volume = AppServices.Volume.Volume;
            muted = AppServices.Volume.Muted;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return;
        }

        var target = Math.Clamp(volume * 100.0, 0, 100);
        // Small differences are just our own write echoing back mid-drag; leave the thumb alone.
        if (Math.Abs(VolumeSlider.Value - target) > 0.5)
        {
            _syncingVolume = true;
            try
            {
                VolumeSlider.Value = target;
            }
            finally
            {
                _syncingVolume = false;
            }
        }
        UpdateVolumeVisuals(muted);
    }

    void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        VolumeText.Text = $"{Math.Round(e.NewValue)}%";
        if (_syncingVolume) return;
        try
        {
            var service = AppServices.Volume;
            if (service is null) return;
            service.Volume = (float)(e.NewValue / 100.0);
            // Like the system flyout: dragging the level up brings the sound back.
            if (service.Muted && e.NewValue > e.OldValue) service.Muted = false;
            UpdateVolumeVisuals(service.Muted);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void ToggleMute()
    {
        try
        {
            var service = AppServices.Volume;
            if (service is null) return;
            service.Muted = !service.Muted;
            UpdateVolumeVisuals(service.Muted);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void UpdateVolumeVisuals(bool muted)
    {
        var silent = muted || VolumeSlider.Value < 0.5;
        MuteIcon.Text = silent ? Glyphs.Mute : Glyphs.Volume;
        MuteIcon.Foreground = (Brush)FindResource(muted ? "DangerBrush" : "TextBrush");
        MuteButton.ToolTip = muted ? "Включить звук" : "Выключить звук";
        VolumeText.Text = $"{Math.Round(VolumeSlider.Value)}%";
    }

    void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
        e.Handled = true;
    }

    // ───────────── Quick actions ─────────────

    static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier?.Flash("Не удалось открыть", target, Glyphs.Info);
        }
    }

    async void StartRegionShot()
    {
        if (_capturing) return;
        _capturing = true;
        var window = Window.GetWindow(this);
        var restoreOpacity = window?.Opacity ?? 1;
        try
        {
            // The snipping overlay freezes the screen as it is, so get the open panel out of the picture first.
            if (window is not null) window.Opacity = 0;
            await Task.Delay(220);
            ShellOpen("ms-screenclip:");
            await Task.Delay(600);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            if (window is not null) window.Opacity = restoreOpacity;
            _capturing = false;
        }
    }

    async void CaptureFullScreen()
    {
        if (_capturing) return;
        _capturing = true;
        var window = Window.GetWindow(this);
        var restoreOpacity = window?.Opacity ?? 1;
        System.Drawing.Bitmap? bitmap = null;
        try
        {
            // Hide the open panel so it isn't in the shot; give the compositor a couple of frames.
            if (window is not null) window.Opacity = 0;
            await Task.Delay(220);

            (string path, bitmap) = await Task.Run(() => CaptureVirtualScreen());

            if (window is not null) window.Opacity = restoreOpacity;
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(bitmap, true, 5, 100);
            }
            catch (Exception ex)
            {
                // The file is saved either way; a locked clipboard isn't worth failing over.
                App.LogError(ex);
            }
            AppServices.Notifier?.Flash("Скриншот сохранён", Path.GetFileName(path), Glyphs.Camera);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier?.Flash("Не удалось сделать скриншот", null, Glyphs.Info);
        }
        finally
        {
            if (window is not null) window.Opacity = restoreOpacity;
            bitmap?.Dispose();
            _capturing = false;
        }
    }

    static (string Path, System.Drawing.Bitmap Bitmap) CaptureVirtualScreen()
    {
        // The process is per-monitor DPI aware, so these are physical pixels across all monitors.
        var x = HomeNative.GetSystemMetrics(HomeNative.SM_XVIRTUALSCREEN);
        var y = HomeNative.GetSystemMetrics(HomeNative.SM_YVIRTUALSCREEN);
        var w = HomeNative.GetSystemMetrics(HomeNative.SM_CXVIRTUALSCREEN);
        var h = HomeNative.GetSystemMetrics(HomeNative.SM_CYVIRTUALSCREEN);

        var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Glint");
            Directory.CreateDirectory(dir);
            var stem = $"Glint {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            var path = Path.Combine(dir, stem + ".png");
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i}).png");

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return (path, bmp);
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    // ───────────── Dock ─────────────

    void RebuildDock()
    {
        var apps = AppServices.Settings?.Current.DockApps ?? new List<string>();
        var signature = string.Join("\n", apps);
        if (signature == _dockSignature) return;
        _dockSignature = signature;

        DockItems.Children.Clear();
        foreach (var path in apps.ToList())
            DockItems.Children.Add(CreateDockButton(path));
        DockHint.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    Button CreateDockButton(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var placeholder = new TextBlock
        {
            Style = (Style)FindResource("Icon"),
            Text = Glyphs.Apps,
            FontSize = 18,
            Foreground = (Brush)FindResource("MutedBrush"),
        };
        var image = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var button = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = string.IsNullOrEmpty(name) ? path : name,
            Content = new Grid { Width = 32, Height = 32, Children = { placeholder, image } },
        };
        button.Click += (_, _) => LaunchDockApp(path, name);

        var remove = new MenuItem { Header = "Убрать" };
        remove.Click += (_, _) => RemoveDockApp(path);
        button.ContextMenu = new ContextMenu { Items = { remove } };

        if (HomeDockIcons.TryGetCached(path) is { } cached)
        {
            image.Source = cached;
            placeholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoadDockIcon(path, image, placeholder);
        }
        return button;
    }

    static async void LoadDockIcon(string path, Image image, TextBlock placeholder)
    {
        try
        {
            if (await HomeDockIcons.LoadAsync(path) is not { } src) return;
            image.Source = src;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static void LaunchDockApp(string path, string name)
    {
        try
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = true };
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                info.WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty;
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier?.Flash("Не удалось запустить", name, Glyphs.Info);
        }
    }

    void RemoveDockApp(string path)
    {
        try
        {
            PinnedApps.Unpin(path);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        RebuildDock();
    }

    void AddDockApps()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Закрепить в доке",
                Filter = "Приложения и ярлыки (*.exe;*.lnk;*.url;*.appref-ms)|*.exe;*.lnk;*.url;*.appref-ms|Все файлы (*.*)|*.*",
                Multiselect = true,
                // Keep .lnk files as-is so the shortcut's arguments and icon survive.
                DereferenceLinks = false,
                // Most shortcuts people want live on the desktop; the shared Start Menu misses per-user installs.
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            var owner = Window.GetWindow(this);
            bool? picked;
            // Without this the panel would collapse behind the dialog as soon as it takes focus.
            Glint.Pill.PillWindow.BeginModal();
            try
            {
                picked = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            }
            finally
            {
                Glint.Pill.PillWindow.EndModal();
            }
            if (picked != true) return;
            PinnedApps.Pin(dialog.FileNames);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        RebuildDock();
    }

    // Dropping shortcuts on the dock pins them; handled here so the pill doesn't send them to the shelf.

    void OnDockDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDockDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        try
        {
            var added = PinnedApps.Pin(paths);
            if (added > 0) AppServices.Notifier.Flash(added == 1 ? "Закреплено" : $"Закреплено: {added}", Path.GetFileNameWithoutExtension(paths[0]), Glyphs.Pin);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        RebuildDock();
    }

    void OnDockWheel(object sender, MouseWheelEventArgs e)
    {
        if (DockScroll.ScrollableWidth <= 0) return;
        DockScroll.ScrollToHorizontalOffset(DockScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }
}
