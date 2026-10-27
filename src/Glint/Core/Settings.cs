using System.Text.Json;

namespace Glint.Core;

public sealed class AppSettings
{
    /// <summary>Pill size multiplier, 0.8..2.0.</summary>
    public double Scale { get; set; } = 1.0;
    public bool HideInFullscreen { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public string CommandBarHotkey { get; set; } = "Alt+Space";
    public int ClipboardHistoryLimit { get; set; } = 200;
    /// <summary>Process names (without .exe) in whose foreground the pill hides.</summary>
    public List<string> HiddenInApps { get; set; } = new();
    /// <summary>Full paths of apps/shortcuts shown in the Home dock.</summary>
    public List<string> DockApps { get; set; } = new();
    /// <summary>Delay before the pill expands on hover, ms.</summary>
    public int HoverDelayMs { get; set; } = 120;
    /// <summary>Horizontal shift of the pill from the screen center, DIPs; set by dragging it.</summary>
    public double PillOffset { get; set; }

    /// <summary>Id of the chosen preset (see ThemeService.Presets) or "custom".</summary>
    public string ThemePreset { get; set; } = "aurora";
    public string AccentHex { get; set; } = "#A78BFA";
    public string Accent2Hex { get; set; } = "#2DD4BF";
    /// <summary>Background tint id: graphite, black, warm, navy, plum.</summary>
    public string SurfaceTint { get; set; } = "graphite";
    /// <summary>Pill background opacity, 0.6..1.0.</summary>
    public double SurfaceOpacity { get; set; } = 0.92;
    /// <summary>"dark", "light" or "system" (follow the Windows app theme). See ThemeService.ThemeModes.</summary>
    public string ThemeMode { get; set; } = "dark";

    /// <summary>The pill slides up off the top edge after a moment, leaving a thin strip to hover.</summary>
    public bool AutoHide { get; set; }
    /// <summary>Clicks go through the collapsed pill; holding Ctrl over it makes it reachable again.</summary>
    public bool ClickThrough { get; set; }
    /// <summary>Asks GitHub for a newer release at launch and every 6 hours. Network, so off by default.</summary>
    public bool AutoUpdateCheck { get; set; }
    /// <summary>
    /// Currency conversion in the command bar ("100 usd", "курс евро"). Asks cbr-xml-daily.ru (fallback
    /// open.er-api.com) at most every 6 hours and only when such a query is typed. Network, so off by default;
    /// the command bar offers to turn it on.
    /// </summary>
    public bool CurrencyRates { get; set; }

    /// <summary>Pill event indicators: mic/camera, keyboard, downloads, OBS, VPN, notifications, lyrics.</summary>
    public IndicatorSettings Indicators { get; set; } = new();

    /// <summary>Screenshots, QR / text from the screen, eyedropper, screen recording (all local).</summary>
    public CaptureSettings Capture { get; set; } = new();

    /// <summary>Open-Meteo weather in the pill and the Погода tab. Network, so off until a city is chosen.</summary>
    public WeatherSettings Weather { get; set; } = new();

    /// <summary>Dictation: language, engine and the optional global hotkey.</summary>
    public VoiceSettings Voice { get; set; } = new();

    /// <summary>Claude Code / Codex status and permission requests in the pill. Local only; off until connected.</summary>
    public AgentSettings Agents { get; set; } = new();

    /// <summary>The character in the pill: eyes on the cursor, dancing, thinking with agents, sleeping. Local only.</summary>
    public MascotSettings Mascot { get; set; } = new();
}

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON under %AppData%\Glint.</summary>
public sealed class SettingsStore
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glint");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly string _path = Path.Combine(DataDir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised on the UI thread after <see cref="Save"/>.</summary>
    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception)
        {
            // A corrupt settings file should never keep the app from starting.
            Current = new();
        }
        // Explicit nulls or junk in a hand-edited file would otherwise break code that expects real values.
        Current.HiddenInApps ??= new();
        Current.DockApps ??= new();
        Current.Indicators ??= new();
        Current.Indicators.Normalize();
        Current.Capture ??= new();
        Current.Capture.Normalize();
        Current.Weather ??= new();
        Current.Weather.Normalize();
        // No forecast without a place, whatever the file says.
        if (!Current.Weather.HasPlace) Current.Weather.Enabled = false;
        Current.Voice ??= new();
        Current.Voice.Normalize();
        Current.Agents ??= new();
        Current.Agents.Normalize();
        Current.Mascot ??= new();
        Current.Mascot.Normalize();
        if (string.IsNullOrWhiteSpace(Current.CommandBarHotkey)) Current.CommandBarHotkey = "Alt+Space";
        if (!double.IsFinite(Current.Scale)) Current.Scale = 1.0;
        Current.Scale = Math.Clamp(Current.Scale, 0.8, 2.0);
        // The pill clamps it to the current screen; this only rejects junk.
        if (!double.IsFinite(Current.PillOffset)) Current.PillOffset = 0;
        // Bad HEX, unknown tint or out-of-range opacity fall back to the defaults.
        Glint.Services.ThemeService.Normalize(Current);
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Helper for other services that persist their own JSON files next to settings.</summary>
    public static T? ReadJson<T>(string fileName)
    {
        try
        {
            var p = Path.Combine(DataDir, fileName);
            return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p)) : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    public static void WriteJson<T>(string fileName, T value)
    {
        Directory.CreateDirectory(DataDir);
        var p = Path.Combine(DataDir, fileName);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(value, JsonOptions));
        File.Move(p + ".tmp", p, overwrite: true);
    }
}
