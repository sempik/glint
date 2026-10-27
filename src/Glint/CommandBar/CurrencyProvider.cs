using System.Globalization;
using System.Text.RegularExpressions;
using Glint.Core;
using Glint.Services;

namespace Glint.CommandBar;

/// <summary>
/// Currency conversion and rates: "100 usd", "100$ в руб", "50 евро to usd", "2,5к юаней", "доллар в рублях",
/// "курс евро", "курс". Rates come from <see cref="CurrencyRates"/>; with the feature off, a matching query gets a
/// single row that turns it on, so nothing goes to the network before the user says so.
/// </summary>
public sealed class CurrencyProvider : ICommandProvider
{
    /// <summary>What to show: <see cref="Amount"/> of each pair's From in its To. Rate queries use 1.</summary>
    public sealed record Request(double Amount, IReadOnlyList<(string From, string To)> Pairs, bool IsRateQuery);

    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    static readonly Regex RateQuery = new(@"^(?:курсы?|rates?|exchange\s+rates?)(?:\s+(?<rest>.+))?$", Opts);
    // " в " / " to " / "→" splits source from target: "100 usd в руб", "100 usd to eur", "доллар в рублях".
    static readonly Regex Target = new(@"^(?<left>.+?)\s*(?:\s(?:в|во|к|на|to|in)\s|=|->|→)\s*(?<right>\S.*)$", Opts);
    static readonly Regex Amount = new(@"^(?<num>[\d\s.,+\-*/()^]*\d[\d\s.,+\-*/()^]*?)\s*(?<mult>к|k|тыс\.?|тысяч[аи]?|млн|mln)?$", Opts);
    static readonly Regex Glued = new(@"^(?<n>[\d.,]+)\s*(?<c>\p{L}+\.?)$", Opts);

    // Exact words and symbols; inflected Russian names go through Stems below.
    static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$"] = "USD", ["€"] = "EUR", ["£"] = "GBP", ["¥"] = "CNY", ["₽"] = "RUB", ["₸"] = "KZT", ["₴"] = "UAH", ["₺"] = "TRY",
        ["р"] = "RUB", ["р."] = "RUB", ["руб"] = "RUB", ["руб."] = "RUB", ["rub"] = "RUB", ["rur"] = "RUB",
        ["dollar"] = "USD", ["dollars"] = "USD", ["bucks"] = "USD", ["euro"] = "EUR", ["euros"] = "EUR",
        ["ruble"] = "RUB", ["rubles"] = "RUB", ["rouble"] = "RUB", ["roubles"] = "RUB", ["pound"] = "GBP", ["pounds"] = "GBP",
        ["yuan"] = "CNY", ["yen"] = "JPY", ["tenge"] = "KZT", ["lira"] = "TRY", ["franc"] = "CHF", ["francs"] = "CHF",
        ["евро"] = "EUR", ["тенге"] = "KZT", ["лари"] = "GEL", ["сум"] = "UZS", ["сума"] = "UZS", ["сумов"] = "UZS",
        ["бат"] = "THB", ["бата"] = "THB", ["батов"] = "THB", ["вон"] = "KRW", ["вона"] = "KRW",
        ["сом"] = "KGS", ["сома"] = "KGS", ["сомов"] = "KGS",
    };

    // Stem plus at most three letters of ending: "долларов", "рублях", "юаней".
    static readonly (string Stem, string Code)[] Stems =
    {
        ("доллар", "USD"), ("бакс", "USD"), ("рубл", "RUB"), ("фунт", "GBP"), ("юан", "CNY"), ("иен", "JPY"), ("йен", "JPY"),
        ("грив", "UAH"), ("лир", "TRY"), ("франк", "CHF"), ("злот", "PLN"), ("драм", "AMD"), ("манат", "AZN"),
        ("дирхам", "AED"), ("рупи", "INR"), ("сомон", "TJS"), ("динар", "RSD"),
    };

    // ISO codes accepted as typed; the rate source decides which of them it actually has.
    static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "USD", "EUR", "RUB", "GBP", "CNY", "JPY", "KZT", "UAH", "BYN", "TRY", "CHF", "PLN", "CZK", "GEL", "AMD", "AZN",
        "KGS", "UZS", "TJS", "MDL", "AED", "INR", "KRW", "THB", "VND", "IDR", "HKD", "SGD", "CAD", "AUD", "NZD", "SEK",
        "NOK", "DKK", "HUF", "RON", "BGN", "RSD", "BRL", "ZAR", "EGP", "QAR", "TMT", "ILS", "MXN", "ARS", "SAR", "MYR",
        "PHP", "ISK",
    };

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        if (Parse(query) is not { } request) return Array.Empty<CommandResult>();
        if (!AppServices.Settings.Current.CurrencyRates) return new[] { EnableRow() };

        var rates = CurrencyRates.Shared.Get();
        if (rates is null)
            return new[] { CurrencyRates.Shared.LastFailed && !CurrencyRates.Shared.IsRefreshing ? FailedRow() : LoadingRow() };

        var list = request.Pairs.Select(p => Row(request, p.From, p.To, rates)).OfType<CommandResult>().ToList();
        if (list.Count == 0)
        {
            var missing = request.Pairs.SelectMany(p => new[] { p.From, p.To }).FirstOrDefault(c => CurrencyRates.RubPer(rates, c) is null) ?? "?";
            list.Add(new CommandResult
            {
                Title = $"Нет курса для {missing}",
                Subtitle = $"{SourceName(rates)}: такой валюты нет в списке",
                Hint = "Валюта",
                Glyph = Glyphs.Currency,
                Score = 950,
                Execute = () => { },
            });
        }
        return list;
    }

    static CommandResult? Row(Request r, string from, string to, CurrencyRates.Snapshot rates)
    {
        if (CurrencyRates.RubPer(rates, from) is not { } fromRub || CurrencyRates.RubPer(rates, to) is not { } toRub) return null;
        var value = r.Amount * fromRub / toRub;
        var copy = Math.Round(value, Math.Abs(value) < 1 ? 4 : 2).ToString("0.####", CultureInfo.CurrentCulture);
        var unit = $"1 {from} = {FormatMoney(fromRub / toRub, to)}";

        string title, subtitle;
        if (r.IsRateQuery)
        {
            title = unit;
            subtitle = $"{Change(rates, from, to)}{SourceName(rates)} · Enter — скопировать";
        }
        else
        {
            title = $"{FormatAmount(r.Amount)} {from} = {FormatMoney(value, to)}";
            subtitle = $"{unit} · {SourceName(rates)} · Enter — скопировать";
        }
        if (CurrencyRates.Shared.IsRefreshing) subtitle += " · обновляю…";
        return new CommandResult
        {
            Title = title,
            Subtitle = subtitle,
            Hint = "Валюта",
            Glyph = Glyphs.Currency,
            Score = 950,
            Execute = () => Copy(copy),
        };
    }

    /// <summary>"▲ 0,34 за день · " against the previous Bank of Russia day; only for rates quoted in rubles.</summary>
    static string Change(CurrencyRates.Snapshot rates, string from, string to)
    {
        if (to != "RUB" || !rates.Rates.TryGetValue(from, out var rate) || rate.Previous is not { } prev) return string.Empty;
        var delta = rate.RubPerUnit - prev;
        if (Math.Abs(delta) < 0.00005) return "без изменений · ";
        return $"{(delta > 0 ? "▲" : "▼")} {Math.Abs(delta).ToString(Math.Abs(delta) < 0.01 ? "0.####" : "0.00", Ru)} за день · ";
    }

    static string SourceName(CurrencyRates.Snapshot s) =>
        s.Source == CurrencyRates.CbrSource ? $"ЦБ РФ на {s.Date:dd.MM}" : $"open.er-api.com, {s.Date:dd.MM HH:mm}";

    static string FormatMoney(double value, string code)
    {
        var abs = Math.Abs(value);
        var format = abs >= 1 || abs == 0 ? "N2" : abs >= 0.01 ? "N4" : "G4";
        var number = value.ToString(format, Ru);
        return code switch
        {
            "RUB" => $"{number} ₽",
            "USD" => $"{number} $",
            "EUR" => $"{number} €",
            _ => $"{number} {code}",
        };
    }

    static string FormatAmount(double v) => v == Math.Floor(v) ? v.ToString("N0", Ru) : v.ToString("N2", Ru);

    static CommandResult EnableRow() => new()
    {
        Title = "Курсы валют выключены",
        Subtitle = "Enter — включить (запрос к cbr-xml-daily.ru не чаще раза в 6 часов)",
        Hint = "Валюта",
        Glyph = Glyphs.Currency,
        Score = 950,
        Execute = () =>
        {
            AppServices.Settings.Current.CurrencyRates = true;
            AppServices.Settings.Save();
            AppServices.Notifier.Flash("Курсы валют включены", "Выключить: Настройки → Командная строка", Glyphs.Currency);
            CurrencyRates.Shared.Get();
            // Back to the same query; the rows fill in when the rates arrive.
            AppServices.ToggleCommandBar();
        },
    };

    static CommandResult LoadingRow() => new()
    {
        Title = "Загружаю курсы валют…",
        Subtitle = "cbr-xml-daily.ru, запасной — open.er-api.com",
        Hint = "Валюта",
        Glyph = Glyphs.Currency,
        Score = 950,
        Execute = () => AppServices.ToggleCommandBar(),
    };

    static CommandResult FailedRow() => new()
    {
        Title = "Не удалось загрузить курсы",
        Subtitle = "Нет сети? Enter — попробовать снова",
        Hint = "Валюта",
        Glyph = Glyphs.Currency,
        Score = 950,
        Execute = () =>
        {
            CurrencyRates.Shared.ForceRefresh();
            AppServices.ToggleCommandBar();
        },
    };

    static void Copy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            AppServices.Notifier.Flash("Скопировано", text, Glyphs.Copy);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier.Flash("Буфер обмена занят", "Попробуйте ещё раз", Glyphs.Info);
        }
    }

    // ───────────── Parsing ─────────────

    /// <summary>Pure: needs no rates, so the "turn it on" row can appear before anything was downloaded.</summary>
    public static Request? Parse(string input)
    {
        var s = input.Trim().TrimEnd('?', '!').Trim();
        if (s.Length < 2) return null;

        // "курс", "курс евро", "курс доллара к евро", "курс рубля".
        var rate = RateQuery.Match(s);
        if (rate.Success)
        {
            var rest = rate.Groups["rest"].Value.Trim();
            if (rest.Length == 0) return new Request(1, new[] { ("USD", "RUB"), ("EUR", "RUB"), ("CNY", "RUB") }, true);
            var split = Target.Match(rest);
            if (split.Success && Resolve(split.Groups["left"].Value) is { } f && Resolve(split.Groups["right"].Value) is { } t && f != t)
                return new Request(1, new[] { (f, t) }, true);
            if (Resolve(rest) is not { } code) return null;
            return code == "RUB"
                ? new Request(1, new[] { ("RUB", "USD"), ("RUB", "EUR") }, true)
                : new Request(1, new[] { (code, "RUB") }, true);
        }

        // Optional explicit target.
        var left = s;
        string? to = null;
        var m = Target.Match(s);
        if (m.Success && Resolve(m.Groups["right"].Value) is { } right)
        {
            left = m.Groups["left"].Value.Trim();
            to = right;
        }

        if (!TrySplitAmount(left, out var amount, out var from, allowNoAmount: to is not null) || from is null || to == from) return null;
        var targets = to is not null
            ? new[] { to }
            : from == "RUB" ? new[] { "USD", "EUR", "CNY" } : from == "USD" ? new[] { "RUB", "EUR" } : new[] { "RUB", "USD" };
        return new Request(amount, targets.Select(t => (from, t)).ToList(), false);
    }

    /// <summary>"100 usd", "$100", "100$", "2,5к юаней"; with <paramref name="allowNoAmount"/> also a bare "доллар" (= 1).</summary>
    static bool TrySplitAmount(string text, out double amount, out string? code, bool allowNoAmount)
    {
        amount = 0;
        code = null;
        var t = text.Trim();
        string numberPart;

        var lastSpace = t.LastIndexOf(' ');
        var tail = lastSpace >= 0 ? t[(lastSpace + 1)..] : t;
        // Two-word names first: "100 белорусских рублей" is not 100 rubles.
        var prevSpace = lastSpace > 0 ? t.LastIndexOf(' ', lastSpace - 1) : -1;
        if (lastSpace > 0 && Resolve(t[(prevSpace + 1)..]) is { } c0)
        {
            code = c0;
            numberPart = prevSpace >= 0 ? t[..prevSpace] : string.Empty;
        }
        else if (Resolve(tail) is { } c1 && (lastSpace >= 0 || IsSymbol(tail) || allowNoAmount))
        {
            // The last word: "100 usd", "1 000 рублей", or the whole thing ("доллар в рублях").
            code = c1;
            numberPart = lastSpace >= 0 ? t[..lastSpace] : string.Empty;
        }
        else if (t.Length > 1 && IsSymbol(t[^1..]))
        {
            code = Resolve(t[^1..]);
            numberPart = t[..^1];
        }
        else if (t.Length > 1 && IsSymbol(t[..1]))
        {
            code = Resolve(t[..1]);
            numberPart = t[1..];
        }
        else
        {
            // Letters glued to the number: "100usd", "100руб".
            var g = Glued.Match(t);
            if (!g.Success || Resolve(g.Groups["c"].Value) is not { } c2) return false;
            code = c2;
            numberPart = g.Groups["n"].Value;
        }

        numberPart = numberPart.Trim();
        if (numberPart.Length == 0)
        {
            if (!allowNoAmount) return false;
            amount = 1;
            return true;
        }
        var a = Amount.Match(numberPart);
        if (!a.Success) return false;
        // "1 000 000" is one number, not three.
        var expr = Regex.Replace(a.Groups["num"].Value.Trim(), @"(?<=\d)\s+(?=\d{3}(?!\d))", "");
        // A plain number, or arithmetic the calculator understands: "3*15 usd".
        if (!Calculator.TryEvaluate("=" + expr, out amount) || !double.IsFinite(amount)) return false;
        amount *= a.Groups["mult"].Value.ToLowerInvariant().TrimEnd('.') switch
        {
            "к" or "k" or "тыс" or "тысяч" or "тысяча" or "тысячи" => 1_000,
            "млн" or "mln" => 1_000_000,
            _ => 1,
        };
        return amount > 0 && amount < 1e15;
    }

    static bool IsSymbol(string s) => s.Length == 1 && "$€£¥₽₸₴₺".Contains(s[0]);

    /// <summary>Currency word, code or symbol to an ISO code, or null.</summary>
    public static string? Resolve(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.Length == 0) return null;
        if (Words.TryGetValue(w, out var code)) return code;
        if (w.Length == 3 && Codes.Contains(w)) return w.ToUpperInvariant();
        // "белорусских рублей", "бел. руб."
        if (w.StartsWith("бел", StringComparison.Ordinal) && w.Contains("руб", StringComparison.Ordinal)) return "BYN";
        if (w.Contains(' ')) return null;
        foreach (var (stem, c) in Stems)
        {
            if (w.StartsWith(stem, StringComparison.Ordinal) && w.Length - stem.Length <= 3) return c;
        }
        return null;
    }
}
