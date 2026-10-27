using System.Security.Cryptography;
using System.Text;

namespace Glint.Core;

/// <summary>
/// Live indicators in the pill (Настройки → Индикаторы). Everything local is on by default and costs nothing
/// while idle; anything that talks to the network or reads other apps' data is off until the user enables it.
/// </summary>
public sealed class IndicatorSettings
{
    /// <summary>Dots while an app uses the microphone or the camera (Windows privacy registry).</summary>
    public bool MicCamera { get; set; } = true;
    public bool CapsLock { get; set; } = true;
    public bool KeyboardLayout { get; set; } = true;
    /// <summary>Browser downloads in the Downloads folder.</summary>
    public bool Downloads { get; set; } = true;
    public bool Steam { get; set; } = true;
    /// <summary>OBS recording, detected from the growing recording file.</summary>
    public bool Obs { get; set; } = true;
    /// <summary>Ask OBS itself over obs-websocket (localhost only) instead of watching the file.</summary>
    public bool ObsWebSocket { get; set; }
    public int ObsWebSocketPort { get; set; } = 4455;
    /// <summary>obs-websocket password encrypted with DPAPI (see <see cref="SecretBox"/>), base64.</summary>
    public string? ObsPasswordProtected { get; set; }
    public bool Vpn { get; set; } = true;
    /// <summary>Look up the public IP and country over HTTPS while a VPN is up (network).</summary>
    public bool VpnIpLookup { get; set; }
    /// <summary>
    /// Adapter names (or descriptions) never counted as a VPN: split tunnels for one app, game LANs.
    /// Starts with "sb-discord", a sing-box split tunnel that carries only Discord yet holds the default route.
    /// </summary>
    public List<string> VpnIgnoredAdapters { get; set; } = new() { "sb-discord" };
    /// <summary>Mirror Windows notifications (app + title) in the pill; reads the system notification store.</summary>
    public bool Notifications { get; set; }
    /// <summary>Synced lyrics for the current track from lrclib.net (network).</summary>
    public bool Lyrics { get; set; }
    /// <summary>Show the current lyrics line next to the clock, not only on the Home tab.</summary>
    public bool LyricsInPill { get; set; } = true;

    /// <summary>Rejects junk from a hand-edited settings file.</summary>
    public void Normalize()
    {
        if (ObsWebSocketPort is < 1 or > 65535) ObsWebSocketPort = 4455;
        VpnIgnoredAdapters = (VpnIgnoredAdapters ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Secrets in settings.json (passwords, API keys) are encrypted with DPAPI for the current Windows user,
/// so a copied settings file is useless on another account or machine. Reuse this for any new secret.
/// </summary>
public static class SecretBox
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Glint.SecretBox.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted by another user or machine, or hand-edited: treat as no secret.
            return null;
        }
    }
}
