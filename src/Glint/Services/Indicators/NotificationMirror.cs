using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Glint.Core;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Glint.Services.Indicators;

/// <summary>
/// Mirrors new Windows notifications in the pill as a short flash (app name and title, never the body).
/// The official UserNotificationListener needs package identity, which an unpackaged exe normally lacks, so
/// when Windows refuses it Glint reads the system notification store (wpndatabase.db) read-only instead.
/// Either way it wakes only when that store's files change. Off by default.
/// </summary>
public sealed class NotificationMirror : IndicatorMonitor
{
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
    static readonly string DbPath = Path.Combine(Folder, "wpndatabase.db");

    static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSEdge"] = "Edge",
        ["Chrome"] = "Chrome",
        ["308046B0AF4A39CB"] = "Firefox",
        ["Telegram"] = "Telegram",
        ["TelegramDesktop"] = "Telegram",
        ["Discord"] = "Discord",
        ["WhatsAppDesktop"] = "WhatsApp",
        ["Explorer"] = "Windows",
        ["SecHealthUI"] = "Безопасность Windows",
        ["WindowsStore"] = "Microsoft Store",
        ["ScreenSketch"] = "Ножницы",
        ["Teams"] = "Teams",
        ["MSTeams"] = "Teams",
        ["Outlook"] = "Outlook",
    };

    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    Timer? _debounce;
    UserNotificationListener? _listener;
    readonly HashSet<uint> _seenListener = new();
    long _lastOrder;
    // False until a baseline read went through; until then every read only takes stock.
    bool _baselined;
    int _generation;

    public NotificationMirror(IndicatorHub hub) : base(hub) { }

    protected override void Start()
    {
        var generation = ++_generation;
        _ = StartAsync(generation);
    }

    async Task StartAsync(int generation)
    {
        // Ask for the supported API first (on the UI thread, where Start runs); on an unpackaged exe this
        // usually throws or says Denied.
        UserNotificationListener? listener = null;
        try
        {
            var candidate = UserNotificationListener.Current;
            if (await candidate.RequestAccessAsync() == UserNotificationListenerAccessStatus.Allowed) listener = candidate;
        }
        catch (Exception)
        {
            // No package identity; fall back to the store.
        }
        if (generation != _generation || !Running) return;

        lock (_gate)
        {
            _listener = listener;
            _seenListener.Clear();
            _baselined = false;
            _lastOrder = 0;
        }
        _debounce = new Timer(_ => _ = PollAsync());
        // Baseline: nothing already in the Action Center is "new".
        await Task.Run(() => PollAsync(baseline: true)).ConfigureAwait(false);

        if (!Directory.Exists(Folder) || generation != _generation || !Running) return;
        // Only the write-ahead log: the service appends every new toast there, while readers like us touch
        // just the -shm file, so watching that one would wake us on our own reads.
        var watcher = new FileSystemWatcher(Folder, "wpndatabase.db-wal")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        // A toast is one short burst of writes; one read after it settles is enough.
        watcher.Changed += (_, _) => _debounce?.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        watcher.Created += (_, _) => _debounce?.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        watcher.EnableRaisingEvents = true;
        lock (_gate)
        {
            if (generation != _generation || !Running)
            {
                watcher.Dispose();
                return;
            }
            _watcher = watcher;
        }
    }

    protected override void Stop()
    {
        lock (_gate)
        {
            _generation++;
            _watcher?.Dispose();
            _watcher = null;
            _listener = null;
        }
        _debounce?.Dispose();
        _debounce = null;
    }

    async Task PollAsync(bool baseline = false)
    {
        if (!Running) return;
        UserNotificationListener? listener;
        lock (_gate)
        {
            listener = _listener;
            // A baseline that failed (store busy at logon) is made up silently, or old toasts would replay as new.
            baseline |= !_baselined;
        }
        try
        {
            var fresh = listener is not null ? await ReadListenerAsync(listener, baseline).ConfigureAwait(false) : ReadStore(baseline);
            // Reached only when the read went through: failures throw.
            lock (_gate) _baselined = true;
            // The toast still lands in the store while Windows keeps quiet; so should the pill.
            if (baseline || !Running || WindowsIsQuiet()) return;
            // A burst (several chats at once) shows only the latest, like the flash queue would anyway.
            if (fresh.LastOrDefault() is { } n)
            {
                var title = fresh.Count > 1 ? $"{n.App} · ещё {fresh.Count - 1}" : n.App;
                AppServices.Notifier.Flash(title, n.Title, Glyphs.Bell, TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ───────────── Supported API ─────────────

    async Task<List<(string App, string? Title)>> ReadListenerAsync(UserNotificationListener listener, bool baseline)
    {
        var result = new List<(string, string?)>();
        var list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        lock (_gate)
        {
            foreach (var n in list)
            {
                if (!_seenListener.Add(n.Id) || baseline) continue;
                string app;
                string? appId;
                try
                {
                    app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Уведомление";
                    appId = n.AppInfo?.AppUserModelId;
                }
                catch (Exception)
                {
                    app = "Уведомление";
                    appId = null;
                }
                if (IsOwn(app) || BannersOff(appId)) continue;
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                var title = binding?.GetTextElements().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                result.Add((app, title));
            }
        }
        return result;
    }

    // ───────────── Notification store ─────────────

    List<(string App, string? Title)> ReadStore(bool baseline)
    {
        var result = new List<(string, string?)>();
        // No store yet: the first toast creates it, so everything in it later is new.
        if (!File.Exists(DbPath)) return result;
        using var db = WinSqlite.OpenReadOnly(DbPath) ?? throw new IOException("Cannot open the notification store");
        if (baseline)
        {
            // [Order] is the store's row counter; Id is random.
            db.Query("SELECT IFNULL(MAX([Order]), 0) FROM Notification", r => _lastOrder = r.Int64(0));
            return result;
        }
        var last = _lastOrder;
        db.Query(
            "SELECT n.[Order], n.Payload, h.PrimaryId FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
            "WHERE n.[Order] > ?1 AND n.Type = 'toast' ORDER BY n.[Order] LIMIT 20",
            r =>
            {
                last = Math.Max(last, r.Int64(0));
                var appId = r.Text(2);
                var app = FriendlyApp(appId);
                if (IsOwn(app) || BannersOff(appId)) return;
                result.Add((app, ToastTitle(r.Blob(1))));
            },
            last);
        // Rows can also be removed and re-added with a higher Order; never step back.
        _lastOrder = Math.Max(_lastOrder, last);
        return result;
    }

    // Glint's own tray balloons are already shown by the pill (or were meant for when it can't).
    static bool IsOwn(string app) => app.Contains("Glint", StringComparison.OrdinalIgnoreCase);

    // ───────────── Windows' own quiet rules ─────────────

    const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    // Focus assist / Do not disturb profile: 0 off, 1 priority only, 2 alarms only. Undocumented but stable since Windows 10.
    const ulong WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED = 0x0D83063EA3BF1C75;

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("ntdll.dll")] static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr scope, out uint changeStamp, out int buffer, ref uint bufferSize);

    /// <summary>True while Windows itself holds banners back: Do not disturb, presentation, full screen, lock screen.</summary>
    static bool WindowsIsQuiet()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0 && state != QUNS_ACCEPTS_NOTIFICATIONS) return true;
        }
        catch (Exception)
        {
            // Fall through to Focus assist.
        }
        try
        {
            var name = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            var size = (uint)sizeof(int);
            return NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, out var profile, ref size) == 0 && size >= sizeof(int) && profile != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The user turned banners (or all notifications) off for this app in Windows settings.</summary>
    static bool BannersOff(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{appId}");
            if (key is null) return false;
            return key.GetValue("ShowBanner") is int banner && banner == 0 || key.GetValue("Enabled") is int enabled && enabled == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>First text line of a toast's XML payload (the title).</summary>
    internal static string? ToastTitle(byte[] payload)
    {
        if (payload.Length == 0) return null;
        try
        {
            var doc = XDocument.Parse(Encoding.UTF8.GetString(payload));
            return doc.Descendants().Where(e => e.Name.LocalName == "text")
                .Select(e => e.Value.Trim()).FirstOrDefault(t => t.Length > 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// "Microsoft.WindowsStore_8wekyb3d8bbwe!App" -> "Microsoft Store", "{GUID}\Telegram Desktop\Telegram.exe" -> "Telegram",
    /// "com.squirrel.Discord.Discord" -> "Discord".
    /// </summary>
    internal static string FriendlyApp(string? primaryId)
    {
        if (string.IsNullOrWhiteSpace(primaryId)) return "Уведомление";
        var name = primaryId.Trim();
        var bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        if (name.Contains('\\') || name.Contains('/'))
        {
            name = Path.GetFileNameWithoutExtension(name.TrimEnd('\\', '/'));
        }
        else
        {
            var underscore = name.IndexOf('_');
            if (underscore > 0) name = name[..underscore];
        }
        if (KnownApps.TryGetValue(name, out var known)) return known;
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        if (KnownApps.TryGetValue(name, out known)) return known;
        return name.Length == 0 ? "Уведомление" : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
