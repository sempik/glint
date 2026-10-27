namespace Glint.Core;

/// <summary>
/// Weather from Open-Meteo (Настройки → Погода). Network, so off until the user picks a city; then one forecast
/// request every 30 minutes while it stays on. The city is stored as a name and coordinates only.
/// </summary>
public sealed class WeatherSettings
{
    public bool Enabled { get; set; }
    /// <summary>Display name of the chosen place ("Москва").</summary>
    public string? Place { get; set; }
    /// <summary>Region and country for the settings card ("Москва, Россия").</summary>
    public string? PlaceDetails { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Temperature and an icon next to the clock; they take turns with the clock when the pill is crowded.</summary>
    public bool ShowInPill { get; set; } = true;
    /// <summary>"Через ~40 мин дождь" about an hour ahead, once per rain.</summary>
    public bool RainAlert { get; set; } = true;

    public bool HasPlace => Latitude is not null && Longitude is not null;

    /// <summary>Rejects junk from a hand-edited settings file.</summary>
    public void Normalize()
    {
        if (Latitude is not double lat || Longitude is not double lon || !double.IsFinite(lat) || !double.IsFinite(lon)
            || lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            Latitude = null;
            Longitude = null;
        }
        if (string.IsNullOrWhiteSpace(Place)) Place = HasPlace ? "Выбранное место" : null;
    }
}

/// <summary>Voice input (Настройки → Голосовой ввод). The mic buttons always work; the global hotkey is off by default.</summary>
public sealed class VoiceSettings
{
    /// <summary>Global dictation hotkey like "Ctrl+Alt+D"; empty means none.</summary>
    public string Hotkey { get; set; } = string.Empty;
    /// <summary>"auto" (the Windows speech language) or a BCP-47 tag such as "ru-RU".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Speech engine id; "windows" is built in (see Services/Voice/DictationService.RegisterEngine).</summary>
    public string Engine { get; set; } = "windows";

    public void Normalize()
    {
        Hotkey = (Hotkey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(Language)) Language = "auto";
        if (string.IsNullOrWhiteSpace(Engine)) Engine = "windows";
    }
}
