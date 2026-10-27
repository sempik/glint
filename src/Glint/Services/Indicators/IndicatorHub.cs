using System.Windows;
using Glint.Core;

namespace Glint.Services.Indicators;

/// <summary>
/// One live status shown as a small badge in the pill and as a chip in the panel header.
/// <see cref="Brush"/> is a theme resource key (AccentBrush, DangerBrush, ...), so badges follow theme changes.
/// </summary>
public sealed record Indicator(string Id, string Glyph, string Brush, string Tooltip)
{
    /// <summary>Short text next to the chip icon in the panel header ("42%", "12:05").</summary>
    public string? Label { get; init; }
    /// <summary>0..1 when known; drawn as a ring around the pill badge.</summary>
    public double? Progress { get; init; }
    /// <summary>What a click on the header chip does; null for a plain status.</summary>
    public Action? Click { get; init; }
    /// <summary>Badges are sorted by this, lowest first (privacy dots lead).</summary>
    public int Order { get; init; }
    /// <summary>Only a header chip, no pill badge: the pill shows this state its own way (screen recording).</summary>
    public bool ChipOnly { get; init; }

    /// <summary>Same content apart from the click handler, which is a fresh lambda on every update.</summary>
    public bool SameAs(Indicator other) =>
        Id == other.Id && Glyph == other.Glyph && Brush == other.Brush && Tooltip == other.Tooltip
        && Label == other.Label && Progress == other.Progress && Order == other.Order && ChipOnly == other.ChipOnly;
}

/// <summary>Base for a source of indicators that can be switched on and off from settings.</summary>
public abstract class IndicatorMonitor : IDisposable
{
    bool _running;

    protected IndicatorHub Hub { get; }

    protected IndicatorMonitor(IndicatorHub hub) => Hub = hub;

    public void SetEnabled(bool enabled)
    {
        if (enabled == _running) return;
        _running = enabled;
        try
        {
            if (enabled) Start();
            else Stop();
        }
        catch (Exception ex)
        {
            // A monitor that can't start (no Steam, no access) must never take the pill down.
            App.LogError(ex);
        }
    }

    /// <summary>Switched on the UI thread, so the hub can trust it when a posted update arrives there.</summary>
    protected internal bool Running => _running;

    /// <summary>Called when the user enabled it. Must be cheap while nothing happens.</summary>
    protected abstract void Start();

    /// <summary>Releases watchers and timers and removes this monitor's indicators.</summary>
    protected abstract void Stop();

    public void Dispose() => SetEnabled(false);
}

/// <summary>
/// Collects indicators from all monitors and turns them on and off as settings change.
/// Monitors may call <see cref="Set"/>/<see cref="Remove"/> from any thread; <see cref="Changed"/> fires on the UI thread.
/// </summary>
public sealed class IndicatorHub : IDisposable
{
    public static IndicatorHub Shared { get; } = new();

    readonly Dictionary<string, Indicator> _items = new();
    readonly List<(Func<IndicatorSettings, bool> Enabled, IndicatorMonitor Monitor)> _monitors = new();
    bool _started;

    /// <summary>Raised on the UI thread when the set of indicators or any of their content changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Current indicators in display order. UI thread only.</summary>
    public IReadOnlyList<Indicator> Items { get; private set; } = Array.Empty<Indicator>();

    public KeyboardMonitor? Keyboard { get; private set; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Add(s => s.MicCamera, new PrivacyMonitor(this));
        Keyboard = new KeyboardMonitor(this);
        Add(s => s.CapsLock || s.KeyboardLayout, Keyboard);
        Add(s => s.Downloads, new DownloadMonitor(this));
        Add(s => s.Steam, new SteamMonitor(this));
        Add(s => s.Obs, new ObsMonitor(this));
        Add(s => s.Vpn, new VpnMonitor(this));
        Add(s => s.Notifications, new NotificationMirror(this));
        Apply();
        AppServices.Settings.Changed += (_, _) => Apply();
    }

    void Add(Func<IndicatorSettings, bool> enabled, IndicatorMonitor monitor) => _monitors.Add((enabled, monitor));

    void Apply()
    {
        var s = AppServices.Settings.Current.Indicators;
        foreach (var (enabled, monitor) in _monitors)
        {
            // Monitors that read settings beyond on/off (OBS mode, IP lookup) restart to pick them up.
            if (monitor is IReconfigurable r && enabled(s) && r.NeedsRestart(s))
            {
                monitor.SetEnabled(false);
            }
            monitor.SetEnabled(enabled(s));
        }
    }

    /// <summary>
    /// Adds or replaces an indicator. A monitor passes itself as <paramref name="owner"/>: its background work
    /// may post an update just before the user switches it off, and that update must not land after the removal.
    /// </summary>
    public void Set(Indicator indicator, IndicatorMonitor? owner = null) => OnUi(() =>
    {
        if (owner is { Running: false }) return;
        if (_items.TryGetValue(indicator.Id, out var old) && old.SameAs(indicator))
        {
            // Keep the newest click handler without repainting anything.
            _items[indicator.Id] = indicator;
            return;
        }
        _items[indicator.Id] = indicator;
        Publish();
    });

    public void Remove(string id) => OnUi(() =>
    {
        if (_items.Remove(id)) Publish();
    });

    /// <summary>Removes every indicator whose id starts with the prefix (a monitor's own namespace).</summary>
    public void RemoveAll(string prefix) => OnUi(() =>
    {
        var gone = _items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var k in gone) _items.Remove(k);
        if (gone.Count > 0) Publish();
    });

    void Publish()
    {
        Items = _items.Values.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
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

    public void Dispose()
    {
        foreach (var (_, monitor) in _monitors) monitor.Dispose();
    }
}

/// <summary>A monitor whose behavior depends on settings beyond its on/off switch.</summary>
public interface IReconfigurable
{
    /// <summary>True when the settings it started with differ from these.</summary>
    bool NeedsRestart(IndicatorSettings settings);
}
