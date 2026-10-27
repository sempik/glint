using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Glint.Core;

namespace Glint.Services;

/// <summary>One pending timer or reminder. A timer is a duration with no text.</summary>
public sealed class Reminder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? Text { get; init; }
    /// <summary>Stored in UTC so a time zone or DST change doesn't move it; shown as local time.</summary>
    public DateTime DueUtc { get; init; }
    /// <summary>What the user asked for (e.g. 5 minutes); shown when a timer runs out.</summary>
    public double TotalSeconds { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Set for a wall-clock time ("в 18:30"): with no text it is still a reminder, not a timer.</summary>
    public bool Absolute { get; init; }

    [JsonIgnore] public DateTime Due => DueUtc.ToLocalTime();
    [JsonIgnore] public bool IsTimer => Text is null && !Absolute;
    /// <summary>What lists call it: its text, or what kind it is.</summary>
    [JsonIgnore] public string Title => Text ?? (IsTimer ? "Таймер" : "Напоминание");

    [JsonIgnore]
    public TimeSpan Remaining
    {
        get
        {
            var r = DueUtc - DateTime.UtcNow;
            return r > TimeSpan.Zero ? r : TimeSpan.Zero;
        }
    }
}

/// <summary>
/// Timers and reminders from the command bar and the Заметки tab. Kept in %AppData%\Glint\reminders.json, so they
/// survive a restart; ones that came due while Glint was closed are shown at the next start. A single one-shot
/// timer is aimed at the nearest one (capped at 30 minutes so a clock change or sleep can't make it miss),
/// so nothing polls in between.
/// </summary>
public sealed class ReminderService
{
    public static ReminderService Shared { get; } = new();

    const string FileName = "reminders.json";
    static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);
    // Above this many missed at once, one summary instead of a queue of flashes.
    const int MaxMissedFlashes = 3;
    // Later than this and it is announced as missed rather than on time.
    static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(1);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly object _gate = new();
    readonly List<Reminder> _items = new();
    DispatcherTimer? _timer;
    bool _started;
    bool _keepFile;

    /// <summary>Raised on the UI thread after a reminder is added, cancelled or fired.</summary>
    public event EventHandler? Changed;

    /// <summary>Thread-safe copy, soonest first.</summary>
    public IReadOnlyList<Reminder> Snapshot()
    {
        lock (_gate) return _items.OrderBy(r => r.DueUtc).ToList();
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>UI thread, once at startup.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += (_, _) => { _timer.Stop(); FireDue(); };

        var saved = LoadSaved();
        AtomicFile.CleanupTemp(SettingsStore.DataDir);
        lock (_gate)
        {
            // Hand-edited or damaged entries are dropped rather than fired at a nonsense time.
            _items.AddRange(saved.Where(r => r is not null && r.DueUtc.Year > 2000)
                .Select(r => new Reminder
                {
                    Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
                    Text = string.IsNullOrWhiteSpace(r.Text) ? null : r.Text.Trim(),
                    DueUtc = DateTime.SpecifyKind(r.DueUtc, DateTimeKind.Utc),
                    TotalSeconds = double.IsFinite(r.TotalSeconds) ? Math.Max(0, r.TotalSeconds) : 0,
                    CreatedUtc = DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc),
                    Absolute = r.Absolute,
                }));
        }

        // Sleep and clock changes leave a DispatcherTimer aimed at the wrong moment; re-aim it.
        Microsoft.Win32.SystemEvents.TimeChanged += (_, _) => OnUi(Reschedule);
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnUi(FireDue);
        };

        FireDue();
    }

    /// <summary>
    /// The saved list. A damaged file is kept as reminders.json.broken before the next save replaces it; one that
    /// can't be read at all (locked) is left alone for this session.
    /// </summary>
    List<Reminder> LoadSaved()
    {
        var path = Path.Combine(SettingsStore.DataDir, FileName);
        string json;
        try
        {
            if (!File.Exists(path)) return new List<Reminder>();
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _keepFile = true;
            return new List<Reminder>();
        }
        try
        {
            return JsonSerializer.Deserialize<List<Reminder>>(json) ?? new List<Reminder>();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            try
            {
                AtomicFile.WriteAllText(path + ".broken", json);
            }
            catch (Exception copyError)
            {
                App.LogError(copyError);
                _keepFile = true;
            }
            return new List<Reminder>();
        }
    }

    /// <summary>
    /// UI thread. Adds and persists a reminder. <paramref name="text"/> null with <paramref name="absolute"/> false
    /// makes it a plain timer.
    /// </summary>
    public Reminder Add(string? text, DateTime dueLocal, TimeSpan total, bool absolute = false)
    {
        var reminder = new Reminder
        {
            Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            DueUtc = dueLocal.ToUniversalTime(),
            TotalSeconds = Math.Max(0, total.TotalSeconds),
            Absolute = absolute,
        };
        lock (_gate) _items.Add(reminder);
        Persist();
        Reschedule();
        RaiseChanged();
        return reminder;
    }

    /// <summary>UI thread. Returns false if it already fired or was cancelled.</summary>
    public bool Cancel(Guid id)
    {
        bool removed;
        lock (_gate) removed = _items.RemoveAll(r => r.Id == id) > 0;
        if (!removed) return false;
        Persist();
        Reschedule();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Announces what is due. Anything well past its time (Glint was closed, or the PC slept) counts as missed,
    /// whoever calls this.
    /// </summary>
    void FireDue()
    {
        List<Reminder> due;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            // Half a second of slack: the timer may tick a hair early.
            due = _items.Where(r => r.DueUtc <= now.AddMilliseconds(500)).OrderBy(r => r.DueUtc).ToList();
            foreach (var r in due) _items.Remove(r);
        }

        if (due.Count > 0)
        {
            Persist();
            try
            {
                var missed = due.Where(r => now - r.DueUtc > MissedAfter).ToList();
                foreach (var r in due.Except(missed)) Announce(r, missed: false);
                if (missed.Count > MaxMissedFlashes)
                {
                    AppServices.Notifier.FlashImportant(
                        $"Пропущено напоминаний: {missed.Count}",
                        string.Join(" · ", missed.Select(r => r.Text ?? (r.IsTimer ? "таймер" : "напоминание")).Take(3)),
                        Glyphs.Clock,
                        TimeSpan.FromSeconds(8));
                }
                else
                {
                    foreach (var r in missed) Announce(r, missed: true);
                }
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            RaiseChanged();
        }
        Reschedule();
    }

    static void Announce(Reminder r, bool missed)
    {
        string subtitle;
        if (missed) subtitle = $"Пропущено, было {Describe(r.Due, DateTime.Now)}";
        else if (r.IsTimer) subtitle = $"Таймер на {FormatDuration(TimeSpan.FromSeconds(r.TotalSeconds))}";
        else if (r.Text is null) subtitle = $"На {r.Due:HH:mm}";
        else subtitle = "Напоминание";
        AppServices.Notifier.FlashImportant(r.Text ?? (r.IsTimer ? "Время вышло" : "Напоминание"), subtitle, Glyphs.Clock, TimeSpan.FromSeconds(8));
    }

    void Reschedule()
    {
        if (_timer is null) return;
        _timer.Stop();
        DateTime? next;
        lock (_gate) next = _items.Count == 0 ? null : _items.Min(r => r.DueUtc);
        if (next is not { } at) return;
        var wait = at - DateTime.UtcNow;
        if (wait < TimeSpan.FromMilliseconds(200)) wait = TimeSpan.FromMilliseconds(200);
        if (wait > MaxWait) wait = MaxWait;
        _timer.Interval = wait;
        _timer.Start();
    }

    void Persist()
    {
        try
        {
            // The saved file couldn't be read at start; writing would replace the reminders in it.
            if (_keepFile) return;
            List<Reminder> copy;
            lock (_gate) copy = _items.OrderBy(r => r.DueUtc).ToList();
            AtomicFile.WriteAllText(Path.Combine(SettingsStore.DataDir, FileName), JsonSerializer.Serialize(copy, JsonOptions));
        }
        catch (Exception ex)
        {
            // A locked file costs persistence across a restart, not the reminder itself.
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
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    // ───────────── Formatting shared by the command bar, Главная and Заметки ─────────────

    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>"сегодня в 18:30", "завтра в 09:00", "в пт, 25.10 в 18:30", "25.12.2027 в 10:00".</summary>
    public static string Describe(DateTime due, DateTime now)
    {
        var days = (due.Date - now.Date).Days;
        var time = due.ToString("HH:mm", Ru);
        return days switch
        {
            0 => $"сегодня в {time}",
            1 => $"завтра в {time}",
            -1 => $"вчера в {time}",
            > 1 and < 7 => $"в {due.ToString("ddd", Ru)}, {due:dd.MM} в {time}",
            _ when due.Year == now.Year => $"{due:dd.MM} в {time}",
            _ => $"{due:dd.MM.yyyy} в {time}",
        };
    }

    /// <summary>Short label for a list row: a countdown within the hour, else <see cref="Describe"/>.</summary>
    public static string DescribeShort(Reminder r, DateTime now)
    {
        var left = r.Remaining;
        if (left < TimeSpan.FromHours(1)) return left.TotalSeconds < 60 ? $"через {Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))} сек" : $"через {(int)Math.Ceiling(left.TotalMinutes)} мин";
        return Describe(r.Due, now);
    }

    public static string FormatDuration(TimeSpan d)
    {
        var total = (long)Math.Round(d.TotalSeconds);
        long days = total / 86400, h = total % 86400 / 3600, m = total % 3600 / 60, s = total % 60;
        var parts = new List<string>(4);
        if (days > 0) parts.Add($"{days} д");
        if (h > 0) parts.Add($"{h} ч");
        if (m > 0) parts.Add($"{m} мин");
        if ((s > 0 && days == 0) || parts.Count == 0) parts.Add($"{s} сек");
        return string.Join(" ", parts);
    }
}
