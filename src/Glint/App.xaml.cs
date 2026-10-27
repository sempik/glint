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
    bool _ownsSingleInstance;
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
        // Right after an update the old copy is still shutting down; wait for it instead of quitting.
        if (!isFirst && e.Args.Contains(UpdateService.UpdatedFlag)) isFirst = WaitForPreviousInstance(_singleInstance);
        if (!isFirst)
        {
            Shutdown();
            return;
        }
        _ownsSingleInstance = true;

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };

        ScrubInheritedEnvironment();
        UpdateService.CleanupOldVersion();
        // Without a Direct3D device the GPU driver doesn't treat Glint as a 3D app, so NVIDIA's desktop recording
        // stops reporting "Glint is preventing desktop capture". A small UI like this renders fine on the CPU.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

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

    /// <summary>
    /// Everything Glint launches inherits its environment. Started from VS Code or another Electron app,
    /// that includes ELECTRON_RUN_AS_NODE=1, which makes every Electron app (Discord, Flow, VS Code...)
    /// start as bare Node and exit at once. Explorer never sets these, so dropping them is safe.
    /// </summary>
    static void ScrubInheritedEnvironment()
    {
        foreach (var name in new[] { "ELECTRON_RUN_AS_NODE", "ELECTRON_NO_ATTACH_CONSOLE", "ELECTRON_NO_ASAR" })
            Environment.SetEnvironmentVariable(name, null);
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

    static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(20));
        }
        catch (AbandonedMutexException)
        {
            // The old process exited without releasing it; the mutex is ours now.
            return true;
        }
    }

    internal void ExitApp()
    {
        IsExiting = true;
        _hotkeys?.Dispose();
        _tray?.Dispose();
        (AppServices.Volume as IDisposable)?.Dispose();
        AudioActivity.Shared.Dispose();
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
        // Hand the mutex over cleanly; an updated copy may already be waiting for it.
        try
        {
            if (_ownsSingleInstance) _singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread after all; disposing below still frees the handle.
        }
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
