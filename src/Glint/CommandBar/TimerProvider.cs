using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Glint.Core;

namespace Glint.CommandBar;

/// <summary>
/// Timers and reminders typed in plain Russian or English: "таймер 5 мин", "1ч 30м", "timer 5m",
/// "напомни через 10 мин позвонить маме", "в 15:30 созвон", "at 15:30 call".
/// </summary>
public sealed class TimerProvider : ICommandProvider
{
    public sealed class ActiveTimer
    {
        public string? Text { get; init; }
        public DateTime Due { get; init; }
        public TimeSpan Total { get; init; }
        internal DispatcherTimer? Timer { get; set; }

        public TimeSpan Remaining
        {
            get
            {
                var r = Due - DateTime.Now;
                return r > TimeSpan.Zero ? r : TimeSpan.Zero;
            }
        }
    }

    sealed record Request(TimeSpan Delay, string? Text, DateTime? At);

    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const double MaxSeconds = 7 * 24 * 3600;

    static readonly Regex Lead = new(
        @"^(?:(?<kw>таймер|timer|напомни(?:ть)?(?:\s+мне)?|remind(?:\s+me)?)(?!\p{L})\s*)?(?:(?<via>через|на|in|for)(?!\p{L})\s*)?", Opts);

    static readonly Regex Part = new(
        @"\G\s*(?:(?:и|and)\s+)?(?<num>\d+(?:[.,]\d+)?)\s*" +
        @"(?<unit>hours|hour|hrs|hr|h|часов|часа|час|ч|minutes|minute|mins|min|m|минуты|минуту|минут|мин|м|seconds|second|secs|sec|s|секунды|секунду|секунд|сек|с)(?!\p{L})", Opts);

    static readonly Regex At = new(
        @"^(?:(?:напомни(?:ть)?(?:\s+мне)?|remind(?:\s+me)?)\s+)?(?:в|at)\s+(?<h>\d{1,2})[:.](?<m>\d{2})(?!\d)\s*(?<text>.*)$", Opts);

    static readonly string[] TimerWords = { "таймер", "timer", "напомин", "remind" };

    static readonly object Gate = new();
    static readonly List<ActiveTimer> Active = new();

    /// <summary>Thread-safe copy of the running timers, soonest first.</summary>
    public static IReadOnlyList<ActiveTimer> Snapshot()
    {
        lock (Gate) return Active.OrderBy(t => t.Due).ToList();
    }

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var q = query.Trim();
        var list = new List<CommandResult>();
        if (Parse(q) is { } request) list.Add(Suggestion(request));
        if (q.Length == 0 || MentionsTimers(q)) list.AddRange(ActiveRows());
        return list;
    }

    static bool MentionsTimers(string q)
    {
        var l = q.ToLowerInvariant();
        return l.Length >= 2 && TimerWords.Any(w => w.StartsWith(l, StringComparison.Ordinal) || l.StartsWith(w, StringComparison.Ordinal));
    }

    static IEnumerable<CommandResult> ActiveRows() => Snapshot().Select(t => new CommandResult
    {
        Title = t.Text is null
            ? $"Активный таймер — осталось {FormatRemaining(t.Remaining)}"
            : $"Напоминание «{t.Text}» — осталось {FormatRemaining(t.Remaining)}",
        Subtitle = "Enter — отменить",
        Hint = $"в {t.Due:HH:mm}",
        Glyph = Glyphs.Clock,
        Execute = () => Cancel(t),
    });

    static CommandResult Suggestion(Request r)
    {
        string title;
        if (r.At is { } at)
            title = r.Text is null ? $"Напомнить в {at:HH:mm}" : $"Напомнить в {at:HH:mm}: {r.Text}";
        else
            title = r.Text is null ? $"Таймер на {FormatDuration(r.Delay)}" : $"Напомнить через {FormatDuration(r.Delay)}: {r.Text}";
        var due = r.At ?? DateTime.Now + r.Delay;
        return new CommandResult
        {
            Title = title,
            Subtitle = $"Enter — запустить · сработает в {due:HH:mm}",
            Hint = "Таймер",
            Glyph = Glyphs.Clock,
            Score = 900,
            Execute = () => Start(r),
        };
    }

    static Request? Parse(string s)
    {
        if (s.Length == 0) return null;

        var at = At.Match(s);
        if (at.Success)
        {
            var h = int.Parse(at.Groups["h"].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(at.Groups["m"].Value, CultureInfo.InvariantCulture);
            if (h > 23 || m > 59) return null;
            var now = DateTime.Now;
            var due = now.Date.AddHours(h).AddMinutes(m);
            if (due <= now) due = due.AddDays(1);
            return new Request(due - now, Clean(at.Groups["text"].Value), due);
        }

        var lead = Lead.Match(s);
        var hasKeyword = lead.Groups["kw"].Success;
        var hasVia = lead.Groups["via"].Success;
        var pos = lead.Success ? lead.Length : 0;

        double seconds = 0;
        var parts = 0;
        for (var m = Part.Match(s, pos); m.Success; m = Part.Match(s, pos))
        {
            var num = double.Parse(m.Groups["num"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            seconds += num * UnitSeconds(m.Groups["unit"].Value);
            pos = m.Index + m.Length;
            parts++;
        }

        var rest = s[pos..].Trim();
        if (parts == 0)
        {
            // "таймер 5": a bare number after the keyword means minutes.
            if (hasKeyword && int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes > 0 && minutes * 60.0 <= MaxSeconds)
                return new Request(TimeSpan.FromMinutes(minutes), null, null);
            return null;
        }

        var explicitIntent = hasKeyword || hasVia;
        // Without a keyword, trailing words mean this is probably a search ("5 мин рецепт").
        if (rest.Length > 0 && !explicitIntent) return null;
        // "1с" is far more likely the 1C app than a one-second timer.
        if (!explicitIntent && seconds < 5) return null;
        if (seconds < 1 || seconds > MaxSeconds) return null;
        return new Request(TimeSpan.FromSeconds(Math.Round(seconds)), Clean(rest), null);
    }

    static double UnitSeconds(string unit) => char.ToLowerInvariant(unit[0]) switch
    {
        'h' or 'ч' => 3600,
        'm' or 'м' => 60,
        _ => 1,
    };

    static string? Clean(string text)
    {
        var t = text.Trim().TrimStart(':', '-', ',', '—').Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>Must run on the UI thread (creates a DispatcherTimer).</summary>
    static void Start(Request r)
    {
        var delay = r.At is { } at ? at - DateTime.Now : r.Delay;
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);

        var timer = new ActiveTimer { Text = r.Text, Due = DateTime.Now + delay, Total = delay };
        var dt = new DispatcherTimer(DispatcherPriority.Normal) { Interval = delay };
        timer.Timer = dt;
        dt.Tick += (_, _) => Fire(timer);
        lock (Gate) Active.Add(timer);
        dt.Start();

        if (r.Text is null)
            AppServices.Notifier.Flash("Таймер запущен", $"{FormatDuration(delay)} · до {timer.Due:HH:mm}", Glyphs.Clock);
        else
            AppServices.Notifier.Flash("Напоминание установлено", $"{r.Text} · в {timer.Due:HH:mm}", Glyphs.Clock);
    }

    static void Fire(ActiveTimer t)
    {
        t.Timer?.Stop();
        lock (Gate)
        {
            if (!Active.Remove(t)) return;
        }
        try
        {
            AppServices.Notifier.FlashImportant(
                t.Text ?? "Время вышло",
                t.Text is null ? $"Таймер на {FormatDuration(t.Total)}" : "Напоминание",
                Glyphs.Clock,
                TimeSpan.FromSeconds(8));
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static void Cancel(ActiveTimer t)
    {
        t.Timer?.Stop();
        lock (Gate) Active.Remove(t);
        AppServices.Notifier.Flash(t.Text is null ? "Таймер отменён" : "Напоминание отменено", t.Text, Glyphs.Clock);
    }

    internal static string FormatDuration(TimeSpan d)
    {
        var total = (long)Math.Round(d.TotalSeconds);
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        var parts = new List<string>(3);
        if (h > 0) parts.Add($"{h} ч");
        if (m > 0) parts.Add($"{m} мин");
        if (s > 0 || parts.Count == 0) parts.Add($"{s} сек");
        return string.Join(" ", parts);
    }

    static string FormatRemaining(TimeSpan r) =>
        r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes}:{r.Seconds:00}";
}
