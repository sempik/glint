using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services.Indicators;

namespace Glint.Services.Voice;

/// <summary>Where dictated text goes: a text box inside Glint, or another app's window.</summary>
public interface IDictationTarget
{
    /// <summary>Short name for messages ("Текст", "Командная строка", "Telegram").</summary>
    string Name { get; }
    /// <summary>False once the box was hidden or the window closed; dictation then stops.</summary>
    bool IsAlive { get; }
    /// <summary>The phrase being spoken right now, or null to clear it. UI thread.</summary>
    void ShowPartial(string? text);
    /// <summary>A finished phrase. UI thread.</summary>
    void AddPhrase(string text);
    /// <summary>Dictation ended (stop, silence, error). Window targets paste what they collected here. UI thread.</summary>
    void Finish();
    /// <summary>
    /// For the fallback when Glint's own recognizer can't run: puts the keyboard focus where the text should go,
    /// so Windows voice typing (Win+H) types there. False if that isn't possible.
    /// </summary>
    bool FocusForVoiceTyping();
}

public enum DictationProblem
{
    /// <summary>No speech recognition language is installed in Windows (or not the one chosen).</summary>
    NoLanguage,
    /// <summary>Windows "Online speech recognition" is off; dictation needs it.</summary>
    PrivacyOff,
    /// <summary>Windows doesn't let desktop apps use the microphone.</summary>
    MicrophoneDenied,
    NoMicrophone,
    Network,
    Other,
}

public sealed class DictationException : Exception
{
    public DictationProblem Problem { get; }

    public DictationException(DictationProblem problem, string? detail = null, Exception? inner = null)
        : base(detail ?? problem.ToString(), inner) => Problem = problem;
}

/// <summary>
/// A speech-to-text engine. The built-in one is <see cref="WindowsSpeechEngine"/>. Events may come on any thread;
/// <see cref="DictationService"/> marshals them to the UI.
/// </summary>
public interface IDictationEngine : IDisposable
{
    event Action<string>? Hypothesis;
    event Action<string>? Phrase;
    /// <summary>The engine stopped by itself (silence, error). The problem is null for a normal end.</summary>
    event Action<DictationProblem?>? Ended;
    /// <summary>Opens the microphone. Throws <see cref="DictationException"/> when it can't.</summary>
    Task StartAsync(string? languageTag);
    /// <summary>Stops listening; phrases still being recognized are delivered before it completes.</summary>
    Task StopAsync();
}

/// <summary>
/// Voice input: the mic buttons in Текст and the command bar, the optional global hotkey and the "диктовка" command.
/// One session at a time. While it listens the pill shows a live line (see PillWindow.Voice.cs) and the panel header
/// a chip; the microphone is released as soon as it stops.
/// <para>
/// Engines are pluggable: an AI provider with transcription can call <see cref="RegisterEngine"/> with its own
/// <see cref="IDictationEngine"/> (record the mic, send it, raise <see cref="IDictationEngine.Phrase"/>), and
/// Настройки → Голосовой ввод lists it. Nothing here depends on such a provider existing.
/// </para>
/// </summary>
public sealed class DictationService
{
    // A forgotten session must not keep the microphone open. Declared before Shared: static initializers run in
    // file order, and the instance built for Shared reads this.
    static readonly TimeSpan MaxSession = TimeSpan.FromMinutes(2);

    public static DictationService Shared { get; } = new();

    public const string WindowsEngineId = "windows";
    const string ChipId = "voice";

    readonly Dictionary<string, (string Name, Func<IDictationEngine> Create)> _engines = new()
    {
        [WindowsEngineId] = ("Распознавание речи Windows", () => new WindowsSpeechEngine()),
    };
    // Bound to the UI dispatcher: the command bar's providers may be the first to touch Shared, off-thread.
    readonly DispatcherTimer _limit = new(DispatcherPriority.Normal, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { Interval = MaxSession };

    IDictationEngine? _engine;
    int _session;
    bool _starting;
    bool _stopRequested;
    int _phrases;

    DictationService()
    {
        _limit.Tick += (_, _) => { _limit.Stop(); Stop(); };
    }

    public bool IsListening => _engine is not null;
    public bool IsStarting => _starting;
    public IDictationTarget? Target { get; private set; }
    /// <summary>The phrase being spoken right now, for the pill's live line.</summary>
    public string? Partial { get; private set; }

    /// <summary>Raised on the UI thread when listening starts or stops, or the live phrase changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Engines for the settings list: id and name.</summary>
    public IReadOnlyList<(string Id, string Name)> Engines => _engines.Select(e => (e.Key, e.Value.Name)).ToList();

    /// <summary>Hook for other speech engines (e.g. an AI provider with transcription). UI thread.</summary>
    public void RegisterEngine(string id, string name, Func<IDictationEngine> create) => _engines[id] = (name, create);

    /// <summary>Starts dictation into the target, or stops it if it's already listening.</summary>
    public void Toggle(IDictationTarget target)
    {
        if (IsListening || _starting) Stop();
        else Start(target);
    }

    /// <summary>Global hotkey and the command bar: dictate into the window the user was typing in.</summary>
    public void ToggleForWindow(IntPtr window)
    {
        if (IsListening || _starting)
        {
            Stop();
            return;
        }
        if (window == IntPtr.Zero)
        {
            AppServices.Notifier.Flash("Некуда диктовать", "Щёлкните в поле ввода и попробуйте снова", Glyphs.Microphone);
            return;
        }
        Start(new WindowDictationTarget(window));
    }

    /// <summary>The foreground window if it's another app's; Glint's own windows fall back to the previous one.</summary>
    public static IntPtr ForegroundTarget()
    {
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(fg, out var pid);
            if (pid != Environment.ProcessId) return fg;
        }
        return AppServices.Clipboard.PreviousWindow;
    }

    public async void Start(IDictationTarget target)
    {
        if (IsListening || _starting) return;
        _starting = true;
        _stopRequested = false;
        _phrases = 0;
        var session = ++_session;
        Target = target;
        Partial = null;
        RaiseChanged();

        var settings = AppServices.Settings.Current.Voice;
        var engineId = _engines.ContainsKey(settings.Engine) ? settings.Engine : WindowsEngineId;
        IDictationEngine? engine = null;
        try
        {
            engine = _engines[engineId].Create();
            engine.Hypothesis += text => OnUi(() => { if (session == _session) SetPartial(text); });
            engine.Phrase += text => OnUi(() => { if (session == _session) AddPhrase(text); });
            engine.Ended += problem => OnUi(() => { if (session == _session) End(problem); });
            await engine.StartAsync(settings.Language == "auto" ? null : settings.Language);
        }
        catch (Exception ex)
        {
            engine?.Dispose();
            // A cancelled start leaves the flag alone: Stop cleared it, and it may belong to a newer start by now.
            if (session != _session) return;
            _starting = false;
            Target = null;
            RaiseChanged();
            var problem = ex is DictationException d ? d.Problem : DictationProblem.Other;
            if (ex is not DictationException) App.LogError(ex);
            Fail(problem, target, ex);
            return;
        }

        // Stop was pressed while the recognizer was still getting ready.
        if (session != _session)
        {
            engine.Dispose();
            return;
        }
        _starting = false;
        _engine = engine;
        _limit.Stop();
        _limit.Start();
        IndicatorHub.Shared.Set(new Indicator(ChipId, Glyphs.Microphone, "DangerBrush", $"Диктовка: {target.Name}. Нажмите, чтобы закончить")
        {
            Label = "Слушаю",
            ChipOnly = true,
            Order = -10,
            Click = Stop,
        });
        RaiseChanged();
    }

    /// <summary>Stops listening; the target gets the last phrases, then <see cref="IDictationTarget.Finish"/>.</summary>
    public async void Stop()
    {
        if (_starting)
        {
            // The start in flight sees the new session number and lets go of the microphone.
            _session++;
            _starting = false;
            var target = Target;
            Target = null;
            target?.Finish();
            RaiseChanged();
            return;
        }
        if (_engine is not { } engine) return;
        _stopRequested = true;
        var session = _session;
        try
        {
            await engine.StopAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        if (session == _session) End(null);
    }

    /// <summary>Stops only if this target is the one listening (a text box that is being hidden).</summary>
    public void StopFor(IDictationTarget target)
    {
        if (ReferenceEquals(Target, target)) Stop();
    }

    void SetPartial(string text)
    {
        if (Target is not { } target) return;
        if (!target.IsAlive)
        {
            Stop();
            return;
        }
        Partial = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        target.ShowPartial(Partial);
        RaiseChanged();
    }

    void AddPhrase(string text)
    {
        if (Target is not { } target || string.IsNullOrWhiteSpace(text)) return;
        Partial = null;
        _phrases++;
        target.ShowPartial(null);
        target.AddPhrase(text.Trim());
        if (!target.IsAlive) Stop();
        else RaiseChanged();
    }

    void End(DictationProblem? problem)
    {
        _session++;
        // The engine can give up while it's still starting.
        _starting = false;
        _limit.Stop();
        var engine = _engine;
        _engine = null;
        var target = Target;
        Target = null;
        Partial = null;
        try
        {
            engine?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        IndicatorHub.Shared.Remove(ChipId);
        try
        {
            target?.ShowPartial(null);
            target?.Finish();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        RaiseChanged();
        if (problem is { } p && target is not null) Fail(p, target, null);
        // Ended on silence before a single phrase: say so, or the mic seems to have done nothing.
        else if (problem is null && !_stopRequested && _phrases == 0 && target is not null)
            AppServices.Notifier.Flash("Ничего не расслышал", "Нажмите микрофон и говорите сразу", Glyphs.Microphone);
    }

    // ───────────── Problems ─────────────

    /// <summary>
    /// Explains what to fix in Windows, with a click that opens the right page. When Windows itself has no speech
    /// language or online recognition is off, Windows voice typing (Win+H) is offered as the fallback: it has its
    /// own models and its own setup prompts, and types wherever the focus is.
    /// </summary>
    static void Fail(DictationProblem problem, IDictationTarget target, Exception? ex)
    {
        var (title, subtitle, page) = Explain(problem);
        var canFallBack = problem is DictationProblem.NoLanguage or DictationProblem.PrivacyOff
            && AppServices.Settings.Current.Voice.Engine == WindowsEngineId;
        if (canFallBack && WindowsVoiceTyping.TryOpen(target))
        {
            // The flash has room for one short line; the full explanation would be cut before what the click opens.
            var hint = problem == DictationProblem.NoLanguage ? "Нет языка распознавания" : "Нет распознавания в сети";
            AppServices.Notifier.FlashClickable("Голосовой ввод Windows", $"{hint}. Нажмите — параметры", Glyphs.Microphone,
                () => OpenSettings(page), TimeSpan.FromSeconds(4));
            return;
        }
        if (problem == DictationProblem.Other && ex is not null) subtitle = "Подробности в error.log";
        AppServices.Notifier.FlashClickable(title, subtitle, Glyphs.Microphone, () => OpenSettings(page), TimeSpan.FromSeconds(6));
    }

    /// <summary>Message and the ms-settings page that fixes it.</summary>
    public static (string Title, string Subtitle, string Page) Explain(DictationProblem problem) => problem switch
    {
        DictationProblem.NoLanguage => ("Нет языка распознавания речи", "Откроются параметры речи Windows", "ms-settings:speech"),
        DictationProblem.PrivacyOff => ("Распознавание речи в сети выключено", "Откроются параметры конфиденциальности", "ms-settings:privacy-speech"),
        DictationProblem.MicrophoneDenied => ("Нет доступа к микрофону", "Разрешите классическим приложениям микрофон", "ms-settings:privacy-microphone"),
        DictationProblem.NoMicrophone => ("Микрофон не найден", "Откроются параметры звука", "ms-settings:sound"),
        DictationProblem.Network => ("Нет связи с распознаванием речи", "Проверьте интернет; параметры речи", "ms-settings:privacy-speech"),
        _ => ("Не удалось начать диктовку", "Откроются параметры речи", "ms-settings:privacy-speech"),
    };

    public static void OpenSettings(string page)
    {
        try
        {
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
