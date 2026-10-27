using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Glint.Services.Control;

/// <summary>What the Пульт tile shows for one radio kind.</summary>
public sealed record RadioInfo(bool Present, bool On, bool CanToggle, string? Problem);

/// <summary>
/// Wi-Fi and Bluetooth on/off through Windows.Devices.Radios, the same switches as the Windows quick settings.
/// Radio objects are kept only while someone listens (the Пульт tab is visible); StateChanged replaces polling.
/// </summary>
public sealed class RadioControl
{
    public static RadioControl Shared { get; } = new();

    readonly SemaphoreSlim _gate = new(1, 1);
    List<Radio>? _radios;
    bool? _access;
    int _listeners;

    /// <summary>Raised on a thread-pool thread when a radio is switched (by Glint or anything else).</summary>
    public event EventHandler? Changed;

    /// <summary>Starts watching for outside changes; pair every call with <see cref="Unlisten"/>.</summary>
    public void Listen()
    {
        if (Interlocked.Increment(ref _listeners) != 1) return;
        StartWatcher();
        _ = LoadAsync();
    }

    public void Unlisten()
    {
        if (Interlocked.Decrement(ref _listeners) > 0) return;
        StopWatcher();
        _ = DropAsync();
    }

    public async Task<RadioInfo> GetAsync(RadioKind kind)
    {
        var radios = await LoadAsync().ConfigureAwait(false);
        var list = radios.Where(r => r.Kind == kind).ToList();
        if (list.Count == 0) return new RadioInfo(false, false, false, kind == RadioKind.WiFi ? "Нет Wi-Fi адаптера" : "Нет Bluetooth");
        if (_access == false) return new RadioInfo(true, list.Any(r => r.State == RadioState.On), false, "Windows запретила переключать радио");
        var on = list.Any(r => r.State == RadioState.On);
        // Disabled means the adapter itself is off in Device Manager or by a hardware switch.
        var disabled = list.All(r => r.State == RadioState.Disabled);
        return new RadioInfo(true, on, !disabled, disabled ? "Адаптер отключён" : null);
    }

    /// <summary>Switches every radio of the kind; returns an error text or null on success.</summary>
    public async Task<string?> SetAsync(RadioKind kind, bool on)
    {
        var radios = await LoadAsync().ConfigureAwait(false);
        var list = radios.Where(r => r.Kind == kind).ToList();
        if (list.Count == 0) return "Адаптер не найден";
        string? error = null;
        foreach (var radio in list)
        {
            try
            {
                var status = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off).AsTask().ConfigureAwait(false);
                error ??= status switch
                {
                    RadioAccessStatus.Allowed => null,
                    RadioAccessStatus.DeniedByUser => "Доступ к радио запрещён в параметрах конфиденциальности",
                    RadioAccessStatus.DeniedBySystem => "Windows не дала переключить (режим «в самолёте» или политика)",
                    _ => "Не удалось переключить",
                };
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                error ??= "Не удалось переключить";
            }
        }
        return error;
    }

    async Task<List<Radio>> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_radios is not null) return _radios;
            try
            {
                // Desktop apps normally get Allowed right away; the answer is remembered for the session.
                _access ??= await Radio.RequestAccessAsync().AsTask().ConfigureAwait(false) == RadioAccessStatus.Allowed;
                var radios = (await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false))
                    .Where(r => r.Kind is RadioKind.WiFi or RadioKind.Bluetooth).ToList();
                foreach (var r in radios) r.StateChanged += OnStateChanged;
                // Cached only while the tab watches; a one-off query is dropped again right away.
                if (Volatile.Read(ref _listeners) > 0) _radios = radios;
                else foreach (var r in radios) r.StateChanged -= OnStateChanged;
                return radios;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return new List<Radio>();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task DropAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _listeners) > 0 || _radios is null) return;
            foreach (var r in _radios) r.StateChanged -= OnStateChanged;
            _radios = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    void OnStateChanged(Radio sender, object args) => Changed?.Invoke(this, EventArgs.Empty);

    // ───────────── Adapters plugged in or out ─────────────

    DeviceWatcher? _watcher;

    /// <summary>A USB Bluetooth dongle or Wi-Fi adapter adds or removes a radio, which the cached list would never show.</summary>
    void StartWatcher()
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(Radio.GetDeviceSelector());
            // The first Added events just list what is already there.
            var ready = false;
            watcher.EnumerationCompleted += (_, _) => ready = true;
            watcher.Added += (_, _) => { if (ready) _ = ReloadAsync(); };
            watcher.Removed += (_, _) => { if (ready) _ = ReloadAsync(); };
            // A watcher without an Updated handler stops reporting additions after the first pass.
            watcher.Updated += (_, _) => { };
            watcher.Start();
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void StopWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    /// <summary>Forgets the cached radios so the next query enumerates again, and tells the tiles to ask.</summary>
    async Task ReloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_radios is not null)
                foreach (var r in _radios) r.StateChanged -= OnStateChanged;
            _radios = null;
        }
        finally
        {
            _gate.Release();
        }
        if (Volatile.Read(ref _listeners) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }
}
