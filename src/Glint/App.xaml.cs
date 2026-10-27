using System.Windows;
using System.Windows.Interop;
using Glint.CommandBar;
using Glint.Core;
using Glint.Pill;
using Glint.Services;
using Glint.Services.Indicators;

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
    int? _voiceHotkeyId;
    string? _registeredVoiceHotkey;
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

        // Pending timers and reminders from the last run; ones that came due while Glint was closed flash now.
        ReminderService.Shared.Start();

        _commandBar = new CommandBarWindow();
        AppServices.ToggleCommandBar = () => _commandBar.Toggle();

        _hotkeys = new HotkeyManager(hwnd);
        RegisterCommandBarHotkey();
        RegisterVoiceHotkey();
        // Only re-register when the gesture really changed, so a taken hotkey isn't reported on every save.
        settings.Changed += (_, _) =>
        {
            if (AppServices.Settings.Current.CommandBarHotkey != _registeredHotkey) RegisterCommandBarHotkey();
            if (AppServices.Settings.Current.Voice.Hotkey != _registeredVoiceHotkey) RegisterVoiceHotkey();
        };

        _tray = new TrayIconService(onExit: ExitApp);
        AppServices.SystemNotify = _tray.ShowBalloon;

        // Pill indicators (mic/camera, keyboard, downloads, OBS, VPN...) and synced lyrics; each part runs
        // only if enabled in Настройки → Индикаторы.
        IndicatorHub.Shared.Start();
        LyricsService.Shared.Start();
        // Off unless Настройки → «Проверять обновления автоматически» is on; then at launch and every 6 hours.
        new UpdateWatcher().Start();
        // Off until a city is chosen in Настройки → Погода; then a forecast every 30 minutes.
        Glint.Services.Weather.WeatherService.Shared.Start();
        // Off until Claude Code or Codex is connected in Настройки → Агенты; then one idle named-pipe accept.
        Glint.Services.Agents.AgentHub.Shared.Start();

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

    /// <summary>Optional global dictation hotkey (Настройки → Голосовой ввод); none by default.</summary>
    void RegisterVoiceHotkey()
    {
        if (_hotkeys is null) return;
        if (_voiceHotkeyId is int old) _hotkeys.Unregister(old);
        _voiceHotkeyId = null;
        var gesture = AppServices.Settings.Current.Voice.Hotkey;
        _registeredVoiceHotkey = gesture;
        if (string.IsNullOrWhiteSpace(gesture)) return;
        // The window in front when the keys are pressed is where the words go.
        _voiceHotkeyId = _hotkeys.Register(gesture, () =>
            Glint.Services.Voice.DictationService.Shared.ToggleForWindow(Glint.Services.Voice.DictationService.ForegroundTarget()));
        if (_voiceHotkeyId is null)
            AppServices.Notifier.Flash("Горячая клавиша диктовки занята", gesture, Glyphs.Microphone, TimeSpan.FromSeconds(4));
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
        // The note being typed is saved a moment after typing stops; don't lose that moment.
        Glint.Pages.NotesPage.FlushAll();
        // A screen recording still running gets its MP4 finished, so the file stays playable.
        Glint.Capture.ScreenTools.StopRecordingForExit();
        // Lets go of the microphone.
        Glint.Services.Voice.DictationService.Shared.Stop();
        // Closes the agent pipe; hooks still waiting get no answer and their agents ask in the terminal.
        Glint.Services.Agents.AgentHub.Shared.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        (AppServices.Volume as IDisposable)?.Dispose();
        AudioActivity.Shared.Dispose();
        // Unhooks the keyboard and stops the watchers.
        IndicatorHub.Shared.Dispose();
        // Releases NVML and the PDH query; only if the Система tab was ever opened.
        if (SystemMonitor.IsSharedCreated) SystemMonitor.Shared.Dispose();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        try
        {
            Glint.Pages.NotesPage.FlushAll();
            Glint.Capture.ScreenTools.StopRecordingForExit();
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
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
