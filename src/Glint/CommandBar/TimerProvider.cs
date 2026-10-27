using System.Globalization;
using System.Text.RegularExpressions;
using Glint.Core;
using Glint.Services;

namespace Glint.CommandBar;

/// <summary>
/// Timers and reminders typed in plain Russian or English: "таймер 5 мин", "1ч 30м", "timer 5m",
/// "напомни через 10 минут позвонить маме", "напомни в 18:30 забрать посылку", "напомни завтра в 9 созвон",
/// "напомни в пятницу в 7 вечера ...", "напомни позвонить маме через час", "remind me tomorrow at 9am to ...".
/// The pending ones live in <see cref="ReminderService"/>, which keeps them across restarts.
/// </summary>
public sealed class TimerProvider : ICommandProvider
{
    /// <summary>A parsed request. <see cref="Absolute"/>: a wall-clock time ("в 18:30"), not a duration from now.</summary>
    public sealed record Request(TimeSpan Delay, string? Text, DateTime Due, bool Absolute);

    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly TimeSpan MaxAhead = TimeSpan.FromDays(366);

    static readonly Regex Keyword = new(
        @"^(?<kw>таймер|timer|напомни(?:ть)?(?:\s+мне)?|напоминание|remind(?:\s+me)?|reminder)(?!\p{L})[\s:,]*", Opts);

    static readonly Regex Via = new(@"\G(?:через|на|in|for|after)(?!\p{L})\s*", Opts);

    static readonly Regex Part = new(
        @"\G\s*(?:(?:и|and)\s+)?(?<num>\d+(?:[.,]\d+)?)\s*" +
        @"(?<unit>недель|недели|неделю|weeks|week|суток|сутки|дней|дня|день|days|day|" +
        @"hours|hour|hrs|hr|h|часов|часа|час|ч|minutes|minute|mins|min|m|минуты|минуту|минут|мин|м|seconds|second|secs|sec|s|секунды|секунду|секунд|сек|с)(?!\p{L})", Opts);

    // "через час", "через полчаса", "через пару минут", "in an hour": a unit word with no digits.
    static readonly Regex WordPart = new(
        @"\G\s*(?<num>полтора|полторы|пол|пару|a|an|one)?\s*(?<unit>часов|часа|час|hours|hour|минуту|минутку|минуты|минут|minutes|minute|секунду|секунд|second|сутки|суток|дней|дня|день|day|неделю|недели|week)(?!\p{L})", Opts);

    static readonly Regex Day = new(
        @"\G(?:(?<rel>послезавтра|завтра|сегодня|day\s+after\s+tomorrow|tomorrow|today|tonight)" +
        @"|(?:(?:в|во|on)\s+)?(?<wd>понедельник|вторник|среду|среда|четверг|пятницу|пятница|субботу|суббота|воскресенье|monday|tuesday|wednesday|thursday|friday|saturday|sunday)" +
        @"|(?<d>\d{1,2})\.(?<mo>\d{1,2})(?:\.(?<y>\d{4}|\d{2}))?(?![\d:.])" +
        @"|(?<d>\d{1,2})\s+(?<mn>января|февраля|марта|апреля|мая|июня|июля|августа|сентября|октября|ноября|декабря))(?!\p{L})", Opts);

    static readonly Regex TimeOfDay = new(
        @"\G(?<tod>утром|днём|днем|вечером|ночью|in\s+the\s+morning|in\s+the\s+evening|morning|evening)(?!\p{L})", Opts);

    static readonly Regex Time = new(
        @"\G(?:(?<pre>в|во|к|at|by)\s+)?(?<h>\d{1,2})(?:(?<sep>[:.])(?<m>\d{2}))?(?!\d)(?!\s*(?:мин|сек|min|sec))" +
        @"\s*(?:(?:часов|часа|час|ч)\.?(?!\p{L})\s*)?(?<ap>утра|дня|вечера|ночи|a\.m\.|p\.m\.|am|pm)?(?!\p{L})", Opts);

    static readonly Regex Separator = new(@"\G[\s,]*(?:(?:и|and)\s+)?", Opts);

    static readonly string[] Months =
        { "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря" };

    static readonly string[] TimerWords = { "таймер", "timer", "напомин", "remind" };

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var q = query.Trim();
        var list = new List<CommandResult>();
        if (Parse(q, DateTime.Now) is { } request) list.Add(Suggestion(request));
        if (q.Length == 0 || MentionsTimers(q)) list.AddRange(ActiveRows());
        return list;
    }

    static bool MentionsTimers(string q)
    {
        var l = q.ToLowerInvariant();
        return l.Length >= 2 && TimerWords.Any(w => w.StartsWith(l, StringComparison.Ordinal) || l.StartsWith(w, StringComparison.Ordinal));
    }

    static IEnumerable<CommandResult> ActiveRows()
    {
        var now = DateTime.Now;
        return ReminderService.Shared.Snapshot().Select(r => new CommandResult
        {
            Title = r.IsTimer ? $"Активный таймер — осталось {FormatRemaining(r.Remaining)}"
                : r.Text is null ? $"Напоминание — {ReminderService.DescribeShort(r, now)}"
                : $"Напоминание «{r.Text}» — {ReminderService.DescribeShort(r, now)}",
            Subtitle = "Enter — отменить",
            Hint = $"в {r.Due:HH:mm}",
            Glyph = Glyphs.Clock,
            Execute = () => Cancel(r),
        });
    }

    static CommandResult Suggestion(Request r)
    {
        string title;
        if (r.Absolute)
            title = r.Text is null ? $"Напомнить {ReminderService.Describe(r.Due, DateTime.Now)}" : $"Напомнить {ReminderService.Describe(r.Due, DateTime.Now)}: {r.Text}";
        else
            title = r.Text is null ? $"Таймер на {ReminderService.FormatDuration(r.Delay)}" : $"Напомнить через {ReminderService.FormatDuration(r.Delay)}: {r.Text}";
        var when = r.Due.Date == DateTime.Today ? $"в {r.Due:HH:mm}" : ReminderService.Describe(r.Due, DateTime.Now);
        return new CommandResult
        {
            Title = title,
            Subtitle = $"Enter — запустить · сработает {when}",
            Hint = r.Text is null && !r.Absolute ? "Таймер" : "Напоминание",
            Glyph = Glyphs.Clock,
            Score = 900,
            Execute = () => Start(r),
        };
    }

    // ───────────── Parsing ─────────────

    /// <summary>
    /// Understands a timer or reminder, or returns null. Pure apart from <paramref name="now"/>, so the Заметки tab
    /// can preview what an entry will do, and a harness can test it.
    /// </summary>
    public static Request? Parse(string input, DateTime now)
    {
        var s = input.Trim().TrimEnd('.', '!', '?');
        if (s.Length == 0) return null;

        var kw = Keyword.Match(s);
        var hasKeyword = kw.Success;
        var pos = hasKeyword ? kw.Length : 0;

        // "таймер 5": a bare number after the keyword means minutes.
        if (hasKeyword && int.TryParse(s[pos..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bare))
            return bare > 0 && bare <= 7 * 24 * 60 ? Relative(TimeSpan.FromMinutes(bare), null, now) : null;

        // When first, text after: "через 10 минут позвонить маме", "завтра в 9 созвон".
        if (TryParseWhen(s, pos, hasKeyword, strictStart: false, now, out var when, out var end))
        {
            var text = Clean(s[end..]);
            if (when.Relative is { } rel)
            {
                // Without a keyword, trailing words mean this is probably a search ("5 мин рецепт").
                if (text is not null && !hasKeyword && !when.HasVia) return null;
                // "1с" is far more likely the 1C app than a one-second timer.
                if (!hasKeyword && !when.HasVia && rel.TotalSeconds < 5) return null;
                return Relative(rel, text, now);
            }
            if (when.Due is { } due) return new Request(due - now, text, due, Absolute: true);
        }

        // Text first, when last: "напомни позвонить маме через час" / "... в 18:30" / "... завтра в 9".
        if (hasKeyword)
        {
            for (var i = pos + 1; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i - 1]) || char.IsWhiteSpace(s[i])) continue;
                if (!TryParseWhen(s, i, hasKeyword: true, strictStart: true, now, out when, out end)) continue;
                if (s[end..].Trim().Length > 0) continue;
                var text = Clean(s[pos..i]);
                if (when.Relative is { } rel) return Relative(rel, text, now);
                if (when.Due is { } due) return new Request(due - now, text, due, Absolute: true);
            }
        }
        return null;
    }

    static Request? Relative(TimeSpan delay, string? text, DateTime now)
    {
        delay = TimeSpan.FromSeconds(Math.Round(delay.TotalSeconds));
        if (delay < TimeSpan.FromSeconds(1) || delay > MaxAhead) return null;
        return new Request(delay, text, now + delay, Absolute: false);
    }

    sealed record When(TimeSpan? Relative, bool HasVia, DateTime? Due);

    /// <summary>
    /// Reads either a duration ("через 1 ч 30 мин") or a day and/or time ("завтра в 9", "в 18:30", "25.12 в 10:00")
    /// starting at <paramref name="pos"/>. <paramref name="strictStart"/> (the text-first form) demands that it
    /// starts with a preposition or a day word, so numbers inside the text aren't taken for times.
    /// </summary>
    static bool TryParseWhen(string s, int pos, bool hasKeyword, bool strictStart, DateTime now, out When when, out int end)
    {
        when = new When(null, false, null);
        end = pos;

        // Duration.
        var via = Via.Match(s, pos);
        var p = via.Success ? via.Index + via.Length : pos;
        if (via.Success || !strictStart)
        {
            double seconds = 0;
            var parts = 0;
            for (var m = Part.Match(s, p); m.Success; m = Part.Match(s, p))
            {
                var num = double.Parse(m.Groups["num"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                seconds += num * UnitSeconds(m.Groups["unit"].Value);
                p = m.Index + m.Length;
                parts++;
            }
            // Unit words without digits only right after "через"/"in": "через час", "через полчаса". Not after a bare
            // keyword: "напомни день рождения 25.12" is about a birthday, not a one-day timer.
            if (parts == 0 && via.Success && WordPart.Match(s, p) is { Success: true } w)
            {
                seconds = WordAmount(w.Groups["num"].Value) * UnitSeconds(w.Groups["unit"].Value);
                p = w.Index + w.Length;
                parts++;
                // "через час 30 минут"
                for (var m = Part.Match(s, p); m.Success; m = Part.Match(s, p))
                {
                    seconds += double.Parse(m.Groups["num"].Value.Replace(',', '.'), CultureInfo.InvariantCulture) * UnitSeconds(m.Groups["unit"].Value);
                    p = m.Index + m.Length;
                }
            }
            if (parts > 0)
            {
                // "через 99999999999 мин" is past what a TimeSpan holds, let alone a year ahead.
                if (!double.IsFinite(seconds) || seconds > MaxAhead.TotalSeconds) return false;
                // A unit glued to more letters ("5 минутная") was already refused by the regex.
                when = new When(TimeSpan.FromSeconds(seconds), via.Success, null);
                end = p;
                return seconds > 0;
            }
        }

        // Day and/or time, in either order.
        DateTime? date = null;
        int? weekday = null;
        bool explicitYear = false, today = false, tonight = false;
        int? hour = null, minute = null;
        var timeHasMinutes = false;
        var timeHasPrefix = false;
        var timeIsWord = false;
        p = pos;
        var first = true;
        while (p < s.Length)
        {
            var sep = Separator.Match(s, p);
            var q = first ? p : sep.Index + sep.Length;
            var matched = false;

            if (date is null && weekday is null && Day.Match(s, q) is { Success: true } d && TryReadDay(d, now, out var day, out var wd, out explicitYear, out today, out tonight))
            {
                date = day;
                weekday = wd;
                q = d.Index + d.Length;
                matched = true;
            }
            else if (hour is null && TimeOfDay.Match(s, q) is { Success: true } tod)
            {
                if (strictStart && first) break;
                hour = tod.Groups["tod"].Value.ToLowerInvariant() switch
                {
                    "утром" or "morning" or "in the morning" => 9,
                    "днём" or "днем" => 13,
                    "ночью" => 23,
                    _ => 19,
                };
                minute = 0;
                timeIsWord = true;
                q = tod.Index + tod.Length;
                matched = true;
            }
            else if (hour is null && Time.Match(s, q) is { Success: true } t && TryReadTime(t, out var h, out var mi))
            {
                var hasPre = t.Groups["pre"].Success;
                var hasMinutes = t.Groups["m"].Success;
                var colon = t.Groups["sep"].Value == ":";
                // A bare number is a time only when it can't be anything else: "18:30", or "18.30" after a keyword.
                if (!hasPre && !t.Groups["ap"].Success && !(hasMinutes && (colon || hasKeyword))) break;
                if (strictStart && first && !hasPre) break;
                hour = h;
                minute = mi;
                timeHasMinutes = hasMinutes || t.Groups["ap"].Success;
                timeHasPrefix = hasPre;
                q = t.Index + t.Length;
                matched = true;
            }

            if (!matched) break;
            p = q;
            first = false;
        }

        if (date is null && weekday is null && hour is null) return false;
        // Without a keyword stay conservative: "завтра погода" or "в 2 раза" are searches.
        if (!hasKeyword)
        {
            var dayGiven = date is not null || weekday is not null;
            if (hour is null) return false;
            if (!timeHasMinutes && !(dayGiven && (timeHasPrefix || timeIsWord))) return false;
        }

        if (tonight && hour is null) { hour = 20; minute = 0; }
        if (today && hour is null) return false;
        var baseDate = date ?? now.Date;
        var due = baseDate.AddHours(hour ?? 9).AddMinutes(minute ?? 0);
        if (weekday is { } target)
        {
            var ahead = ((target - (int)now.DayOfWeek) % 7 + 7) % 7;
            due = now.Date.AddDays(ahead).AddHours(hour ?? 9).AddMinutes(minute ?? 0);
            if (due <= now) due = due.AddDays(7);
        }
        else if (date is null)
        {
            // "в 9" when it's already past nine means tomorrow morning.
            if (due <= now) due = due.AddDays(1);
        }
        else if (due <= now && !explicitYear && !today && (now.Date - baseDate).Days > 1)
        {
            // "25.01" in December: next January.
            due = due.AddYears(1);
        }

        if (due <= now || due - now > MaxAhead) return false;
        when = new When(null, false, due);
        end = p;
        return true;
    }

    static bool TryReadDay(Match d, DateTime now, out DateTime? date, out int? weekday, out bool explicitYear, out bool today, out bool tonight)
    {
        date = null;
        weekday = null;
        explicitYear = false;
        today = false;
        tonight = false;
        if (d.Groups["rel"].Success)
        {
            var rel = Regex.Replace(d.Groups["rel"].Value.ToLowerInvariant(), @"\s+", " ");
            switch (rel)
            {
                case "сегодня" or "today":
                    today = true;
                    date = now.Date;
                    break;
                case "tonight":
                    tonight = true;
                    date = now.Date;
                    break;
                case "завтра" or "tomorrow":
                    date = now.Date.AddDays(1);
                    break;
                default:
                    date = now.Date.AddDays(2);
                    break;
            }
            return true;
        }
        if (d.Groups["wd"].Success)
        {
            var w = d.Groups["wd"].Value.ToLowerInvariant();
            weekday = w switch
            {
                "понедельник" or "monday" => 1,
                "вторник" or "tuesday" => 2,
                "среду" or "среда" or "wednesday" => 3,
                "четверг" or "thursday" => 4,
                "пятницу" or "пятница" or "friday" => 5,
                "субботу" or "суббота" or "saturday" => 6,
                _ => 0,
            };
            return true;
        }
        if (!int.TryParse(d.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var dd)) return false;
        int month;
        if (d.Groups["mn"].Success) month = Array.IndexOf(Months, d.Groups["mn"].Value.ToLowerInvariant()) + 1;
        else if (!int.TryParse(d.Groups["mo"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out month)) return false;
        var year = now.Year;
        if (d.Groups["y"].Success && int.TryParse(d.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y))
        {
            year = y < 100 ? 2000 + y : y;
            explicitYear = true;
        }
        if (year is < 1 or > 9999 || month is < 1 or > 12 || dd < 1 || dd > DateTime.DaysInMonth(year, month)) return false;
        date = new DateTime(year, month, dd);
        return true;
    }

    static bool TryReadTime(Match t, out int hour, out int minute)
    {
        hour = int.Parse(t.Groups["h"].Value, CultureInfo.InvariantCulture);
        minute = t.Groups["m"].Success ? int.Parse(t.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var ap = t.Groups["ap"].Value.ToLowerInvariant().Replace(".", "");
        switch (ap)
        {
            case "pm":
                if (hour is < 1 or > 12) return false;
                if (hour < 12) hour += 12;
                break;
            case "am":
                if (hour is < 1 or > 12) return false;
                if (hour == 12) hour = 0;
                break;
            case "вечера":
                if (hour is >= 1 and <= 11) hour += 12;
                break;
            case "дня":
                if (hour is >= 1 and <= 6) hour += 12;
                break;
            case "ночи":
                if (hour == 12) hour = 0;
                else if (hour is >= 9 and <= 11) hour += 12;
                break;
            case "утра":
                if (hour == 12) hour = 0;
                break;
        }
        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }

    static double UnitSeconds(string unit)
    {
        var u = unit.ToLowerInvariant();
        if (u.StartsWith("нед", StringComparison.Ordinal) || u.StartsWith("week", StringComparison.Ordinal)) return 7 * 86400;
        if (u.StartsWith("сут", StringComparison.Ordinal) || u.StartsWith("д", StringComparison.Ordinal) || u.StartsWith("day", StringComparison.Ordinal)) return 86400;
        return u[0] switch
        {
            'h' or 'ч' => 3600,
            'm' or 'м' => 60,
            _ => 1,
        };
    }

    static double WordAmount(string word) => word.ToLowerInvariant() switch
    {
        "полтора" or "полторы" => 1.5,
        "пол" => 0.5,
        "пару" => 2,
        _ => 1,
    };

    static readonly Regex Filler = new(@"^(?:о\s+том,?\s+что|что(?:бы)?|про|to|about|that)\s+", Opts);

    static string? Clean(string text)
    {
        var t = text.Trim().Trim(':', '-', ',', '—', '.').Trim();
        t = Filler.Replace(t, "").Trim();
        return t.Length == 0 ? null : t;
    }

    // ───────────── Actions ─────────────

    /// <summary>Must run on the UI thread. Relative requests count from the moment Enter is pressed.</summary>
    public static Reminder Start(Request r)
    {
        var due = r.Absolute ? r.Due : DateTime.Now + r.Delay;
        if (due < DateTime.Now.AddSeconds(1)) due = DateTime.Now.AddSeconds(1);
        var reminder = ReminderService.Shared.Add(r.Text, due, r.Absolute ? due - DateTime.Now : r.Delay, r.Absolute);

        if (r.Text is null && !r.Absolute)
            AppServices.Notifier.Flash("Таймер запущен", $"{ReminderService.FormatDuration(r.Delay)} · до {due:HH:mm}", Glyphs.Clock);
        else
            AppServices.Notifier.Flash("Напоминание установлено", $"{r.Text ?? "Без текста"} · {ReminderService.Describe(due, DateTime.Now)}", Glyphs.Clock);
        return reminder;
    }

    static void Cancel(Reminder r)
    {
        if (!ReminderService.Shared.Cancel(r.Id)) return;
        AppServices.Notifier.Flash(r.IsTimer ? "Таймер отменён" : "Напоминание отменено", r.Text, Glyphs.Clock);
    }

    static string FormatRemaining(TimeSpan r) =>
        r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes}:{r.Seconds:00}";
}
