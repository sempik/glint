using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glint.Services.Control;

/// <summary>A VPN connection from the Windows phonebook (Параметры → Сеть → VPN).</summary>
public sealed record VpnProfile(string Name, string Phonebook, bool Connected);

/// <summary>
/// Windows' own VPN connections. Profiles are read from the per-user and all-users rasphone.pbk, the live state
/// comes from RasEnumConnections, and connecting uses rasdial with the saved credentials; when Windows has none
/// saved, the system connect dialog opens instead.
/// </summary>
public static class VpnProfiles
{
    const string PbkTail = @"Microsoft\Network\Connections\Pbk\rasphone.pbk";

    static IEnumerable<string> Phonebooks => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), PbkTail),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), PbkTail),
    };

    public static List<VpnProfile> List()
    {
        var connected = ConnectedNames();
        var result = new List<VpnProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pbk in Phonebooks)
            foreach (var name in ReadVpnEntries(pbk))
                if (seen.Add(name)) result.Add(new VpnProfile(name, pbk, connected.Contains(name)));
        return result;
    }

    /// <summary>
    /// Entry names whose Type is VPN (2). A phonebook is an INI file: "[Name]" opens an entry, "Type=" follows.
    /// Entries without a Type line are kept, older Windows versions wrote none.
    /// </summary>
    internal static IEnumerable<string> ReadVpnEntries(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path)) yield break;
            lines = File.ReadAllLines(path);
        }
        catch (Exception)
        {
            yield break;
        }
        string? current = null;
        string? type = null;
        foreach (var raw in lines.Append("[]"))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (current is { Length: > 0 } && (type is null || type == "2")) yield return current;
                current = line[1..^1];
                type = null;
            }
            else if (current is not null && type is null && line.StartsWith("Type=", StringComparison.OrdinalIgnoreCase))
            {
                type = line[5..].Trim();
            }
        }
    }

    /// <summary>Connects (or disconnects) and waits for rasdial; call it off the UI thread. Returns an error or null.</summary>
    public static string? SetConnected(VpnProfile profile, bool connect)
    {
        if (!connect)
            return RunRasdial($"\"{profile.Name}\" /disconnect", out _) == 0 ? null : "Не удалось отключить";

        var code = RunRasdial($"\"{profile.Name}\" /phonebook:\"{profile.Phonebook}\"", out var timedOut);
        if (code == 0) return null;
        if (timedOut) return "VPN не ответил за минуту";
        // 691: wrong or missing credentials, 703/5: needs user input (EAP, certificates). The Windows dialog asks.
        try
        {
            Process.Start(new ProcessStartInfo("rasphone.exe", $"-d \"{profile.Name}\"") { UseShellExecute = false })?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return $"Ошибка подключения ({code})";
        }
    }

    static int RunRasdial(string arguments, out bool timedOut)
    {
        timedOut = false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("rasdial.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return -1;
            // Drain the pipes so a chatty rasdial never blocks on a full buffer.
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000))
            {
                timedOut = true;
                try { p.Kill(); } catch (Exception) { /* already gone */ }
                return -1;
            }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return -1;
        }
    }

    // ───────────── RasEnumConnections ─────────────

    /// <summary>Names of the RAS connections that are up right now.</summary>
    internal static HashSet<string> ConnectedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var size = Marshal.SizeOf<RASCONN>();
        var count = 4;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var bytes = size * count;
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                // Every slot needs its dwSize; Windows checks the first one to pick the structure version.
                for (var i = 0; i < count; i++) Marshal.WriteInt32(buffer + i * size, size);
                var rc = RasEnumConnections(buffer, ref bytes, out var found);
                if (rc == ERROR_BUFFER_TOO_SMALL)
                {
                    count = Math.Max(count * 2, bytes / size + 1);
                    continue;
                }
                if (rc != 0) return names;
                for (var i = 0; i < found; i++)
                    names.Add(Marshal.PtrToStructure<RASCONN>(buffer + i * size).szEntryName);
                return names;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return names;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return names;
    }

    const int ERROR_BUFFER_TOO_SMALL = 603;

    // The Windows 7+ layout; 1392 bytes on x64.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RASCONN
    {
        public int dwSize;
        public IntPtr hrasconn;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string szEntryName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string szDeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string szDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPhonebook;
        public int dwSubEntry;
        public Guid guidEntry;
        public int dwFlags;
        public long luid;
        public Guid guidCorrelationId;
    }

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RasEnumConnectionsW")]
    static extern int RasEnumConnections(IntPtr connections, ref int size, out int count);
}
