using System.Runtime.InteropServices;
using Glint.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Glint.Services.Indicators;

/// <summary>
/// Microphone and camera in use, read from the same registry Windows uses for its own privacy icon:
/// every app that opens the device gets LastUsedTimeStart, and LastUsedTimeStop stays 0 until it lets go.
/// A dedicated thread sleeps on RegNotifyChangeKeyValue, so nothing runs until the registry changes.
/// </summary>
public sealed class PrivacyMonitor : IndicatorMonitor
{
    const string StorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    const string IdPrefix = "privacy.";
    const int REG_NOTIFY_CHANGE_NAME = 0x1;
    const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, int filter, SafeWaitHandle evt, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    Thread? _thread;
    ManualResetEvent? _stop;
    HashSet<string> _mic = new(), _cam = new();

    public PrivacyMonitor(IndicatorHub hub) : base(hub) { }

    protected override void Start()
    {
        _stop = new ManualResetEvent(false);
        var stop = _stop;
        // A dedicated thread: an asynchronous registry notification is cancelled when the thread that
        // armed it exits, which pool threads are free to do.
        _thread = new Thread(() => WatchLoop(stop)) { IsBackground = true, Name = "Glint privacy watch", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    protected override void Stop()
    {
        try
        {
            _stop?.Set();
        }
        catch (ObjectDisposedException)
        {
            // The thread already ended (no ConsentStore key) and released it.
        }
        _thread = null;
        _stop = null;
        _mic = new();
        _cam = new();
        Hub.RemoveAll(IdPrefix);
    }

    void WatchLoop(ManualResetEvent stop)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StorePath);
            if (key is null) return;
            using var changed = new AutoResetEvent(false);
            Scan(initial: true);
            while (true)
            {
                if (RegNotifyChangeKeyValue(key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true) != 0) return;
                if (WaitHandle.WaitAny(new WaitHandle[] { stop, changed }) == 0) return;
                // Apps write start and stop stamps in bursts; settle before reading.
                if (stop.WaitOne(300)) return;
                Scan(initial: false);
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            stop.Dispose();
        }
    }

    void Scan(bool initial)
    {
        var mic = ActiveApps("microphone");
        var cam = ActiveApps("webcam");
        Report("mic", mic, ref _mic, Glyphs.Microphone, "WarningBrush", "Микрофон", 0, initial);
        Report("cam", cam, ref _cam, Glyphs.Webcam, "SuccessBrush", "Камера", 1, initial);
    }

    void Report(string id, HashSet<string> now, ref HashSet<string> before, string glyph, string brush, string what, int order, bool initial)
    {
        // A scan still in flight when the user switched the indicator off must not bring the dot back.
        if (!Running) return;
        var started = now.Except(before).ToList();
        before = now;
        if (now.Count == 0)
        {
            Hub.Remove(IdPrefix + id);
            return;
        }
        var apps = string.Join(", ", now.OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase));
        Hub.Set(new Indicator(IdPrefix + id, glyph, brush, $"{what}: {apps}") { Order = order }, this);
        // Say once who just took the device; the dot stays as long as it's in use.
        if (!initial && started.Count > 0)
            AppServices.Notifier.Flash($"{what} используется", string.Join(", ", started), glyph, TimeSpan.FromSeconds(2.2));
    }

    /// <summary>Friendly names of apps holding the device right now.</summary>
    static HashSet<string> ActiveApps(string capability)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey($@"{StorePath}\{capability}");
            if (root is null) return result;
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                if (sub is null) continue;
                if (name == "NonPackaged")
                {
                    // Desktop apps are keyed by their exe path with '#' for '\'.
                    foreach (var exe in sub.GetSubKeyNames())
                    {
                        // Glint's own dictation has its own live mark in the pill.
                        if (string.Equals(exe, OwnKey, StringComparison.OrdinalIgnoreCase)) continue;
                        using var app = sub.OpenSubKey(exe);
                        if (app is not null && InUse(app)) result.Add(DesktopName(exe));
                    }
                }
                else if (InUse(sub))
                {
                    result.Add(PackagedName(name));
                }
            }
        }
        catch (Exception)
        {
            // Keys come and go while apps start and stop; the next change rescans.
        }
        return result;
    }

    static readonly string OwnKey = (Environment.ProcessPath ?? string.Empty).Replace('\\', '#');

    static bool InUse(RegistryKey app) =>
        app.GetValue("LastUsedTimeStart") is long start && start != 0 && app.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    /// <summary>"C:#Program Files#Discord#app-1.0#Discord.exe" -> "Discord".</summary>
    static string DesktopName(string key)
    {
        var path = key.Replace('#', '\\');
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
        }
        catch (Exception)
        {
            // The exe may be gone or unreadable; the file name will do.
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera".</summary>
    static string PackagedName(string family)
    {
        var name = family;
        var underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name;
    }
}
