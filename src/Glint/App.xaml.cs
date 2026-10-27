using System.Windows;
using System.Windows.Interop;
using Glint.CommandBar;
using Glint.Core;
using Glint.Pill;
using Glint.Services;

namespace Glint;

public partial class App : Application
{
    Mutex? _singleInstance;
    PillWindow? _pill;
    CommandBarWindow? _commandBar;
    HotkeyManager? _hotkeys;
    int? _commandBarHotkeyId;
    string? _registeredHotkey;
    TrayIconService? _tray;

    /// <summary>Set once the user chose Exit; windows that normally refuse to close check it.</summary>
    internal static bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Glint.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };

        var settings = new SettingsStore();
        settings.Load();
        AppServices.Settings = settings;
        ThemeService.Apply(settings.Current);
        // Apply skips work when the colors didn't change, so this is cheap for unrelated saves.
        settings.Changed += (_, _) => ThemeService.Apply(settings.Current);

        AppServices.Volume = new VolumeService();
        AppServices.Shelf = new ShelfService();
        var clipboard = new ClipboardService();
        AppServices.Clipboard = clipboard;
        var media = new MediaService();
        AppServices.Media = media;

        _pill = new PillWindow();
        AppServices.Notifier = _pill;
        _pill.Show();

        var hwnd = new WindowInteropHelper(_pill).Handle;
        clipboard.Start(hwnd);

        _commandBar = new CommandBarWindow();
        AppServices.ToggleCommandBar = () => _commandBar.Toggle();

        _hotkeys = new HotkeyManager(hwnd);
        RegisterCommandBarHotkey();
        // Only re-register when the gesture really changed, so a taken hotkey isn't reported on every save.
        settings.Changed += (_, _) =>
        {
            if (AppServices.Settings.Current.CommandBarHotkey != _registeredHotkey) RegisterCommandBarHotkey();
        };

        _tray = new TrayIconService(onExit: ExitApp);
        AppServices.SystemNotify = _tray.ShowBalloon;

        try
        {
            await media.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Media controls are optional; the rest of the pill still works.
            LogError(ex);
        }
    }

    void RegisterCommandBarHotkey()
    {
        if (_hotkeys is null) return;
        if (_commandBarHotkeyId is int old) _hotkeys.Unregister(old);
        var gesture = AppServices.Settings.Current.CommandBarHotkey;
        _registeredHotkey = gesture;
        _commandBarHotkeyId = _hotkeys.Register(gesture, () => AppServices.ToggleCommandBar());
        if (_commandBarHotkeyId is null)
            AppServices.Notifier.Flash("Горячая клавиша занята", gesture, Glyphs.Info, TimeSpan.FromSeconds(4));
    }

    void ExitApp()
    {
        IsExiting = true;
        _hotkeys?.Dispose();
        _tray?.Dispose();
        (AppServices.Volume as IDisposable)?.Dispose();
        // Releases NVML and the PDH query; only if the Система tab was ever opened.
        if (SystemMonitor.IsSharedCreated) SystemMonitor.Shared.Dispose();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    internal static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDir);
            File.AppendAllText(Path.Combine(SettingsStore.DataDir, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch (Exception)
        {
            // Logging must never throw.
        }
    }
}
