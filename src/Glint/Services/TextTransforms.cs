using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glint.Services;

/// <summary>Pure text conversions used by the text tools page and the command bar.</summary>
public static class TextTransforms
{
    // ───────────── Keyboard layout ─────────────

    // Same physical key, US QWERTY vs Russian ЙЦУКЕН. Index-aligned.
    const string UsKeys = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./" + "~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?" + "@#$^&|";
    const string RuKeys = "ёйцукенгшщзхъфывапролджэячсмитьбю." + "ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ," + "\"№;:?/";

    static readonly Dictionary<char, char> s_toRu = BuildMap(UsKeys, RuKeys);
    static readonly Dictionary<char, char> s_toUs = BuildMap(RuKeys, UsKeys);

    static Dictionary<char, char> BuildMap(string from, string to)
    {
        var map = new Dictionary<char, char>(from.Length);
        for (var i = 0; i < from.Length; i++) map[from[i]] = to[i];
        return map;
    }

    /// <summary>Retypes text as if it had been entered in the other layout. Direction is picked by the dominant script.</summary>
    public static string SwitchLayout(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        int latin = 0, cyrillic = 0;
        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
            else if (c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') cyrillic++;
        }
        var map = latin >= cyrillic ? s_toRu : s_toUs;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(map.TryGetValue(c, out var m) ? m : c);
        return sb.ToString();
    }

    // ───────────── Case ─────────────

    public static string Upper(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.ToUpper(CultureInfo.InvariantCulture);

    public static string Lower(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.ToLower(CultureInfo.InvariantCulture);

    public static string TitleCase(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var culture = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(text.Length);
        var prevIsWordChar = false;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
                sb.Append(prevIsWordChar ? char.ToLower(c, culture) : char.ToUpper(c, culture));
            else
                sb.Append(c);
            // Apostrophes stay inside a word: "don't" -> "Don't", not "Don'T".
            prevIsWordChar = char.IsLetterOrDigit(c) || (prevIsWordChar && c is '\'' or '’');
        }
        return sb.ToString();
    }

    public static string SentenceCase(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var culture = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(text.Length);
        var capitalizeNext = true;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(capitalizeNext ? char.ToUpper(c, culture) : char.ToLower(c, culture));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(c);
                if (char.IsDigit(c)) capitalizeNext = false;
                else if (c is '.' or '!' or '?' or '…') capitalizeNext = true;
            }
        }
        return sb.ToString();
    }

    // ───────────── Transliteration ─────────────

    static readonly Dictionary<char, string> s_translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    public static string Transliterate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lower = char.ToLowerInvariant(c);
            if (!s_translit.TryGetValue(lower, out var latin))
            {
                sb.Append(c);
                continue;
            }
            if (c == lower || latin.Length == 0)
            {
                sb.Append(latin);
                continue;
            }
            // "Щука" -> "Shchuka", but "ЩУКА" -> "SHCHUKA": look at neighbouring letters to spot all-caps words.
            if (latin.Length > 1 && IsAllCapsContext(text, i))
                sb.Append(latin.ToUpperInvariant());
            else
                sb.Append(char.ToUpperInvariant(latin[0])).Append(latin, 1, latin.Length - 1);
        }
        return sb.ToString();
    }

    static bool IsAllCapsContext(string text, int i)
    {
        if (i + 1 < text.Length && char.IsLetter(text[i + 1])) return char.IsUpper(text[i + 1]);
        return i > 0 && char.IsLetter(text[i - 1]) && char.IsUpper(text[i - 1]);
    }

    // ───────────── Whitespace ─────────────

    static readonly Regex s_lineBreaks = new(@"[ \t]*(?:\r\n|\r|\n)+[ \t]*", RegexOptions.Compiled);
    static readonly Regex s_spaceRuns = new(@"[ \t ]+", RegexOptions.Compiled);
    static readonly Regex s_lineEdges = new(@"^ +| +(?=\r?$)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Joins all lines into one, separated by single spaces.</summary>
    public static string RemoveLineBreaks(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return s_lineBreaks.Replace(text, " ").Trim();
    }

    /// <summary>Collapses runs of spaces/tabs to one space and trims every line; line breaks are kept.</summary>
    public static string CollapseSpaces(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return s_lineEdges.Replace(s_spaceRuns.Replace(text, " "), string.Empty);
    }

    // ───────────── JSON ─────────────

    public static string JsonPretty(string? text) => ReformatJson(text, indented: true);

    public static string JsonMinify(string? text) => ReformatJson(text, indented: false);

    static string ReformatJson(string? text, bool indented)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = indented,
                // Keep Cyrillic and other non-ASCII readable instead of \uXXXX.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                doc.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is long line
                ? $" (строка {line + 1}, позиция {(ex.BytePositionInLine ?? 0) + 1})"
                : string.Empty;
            throw new FormatException("Это не похоже на корректный JSON" + where, ex);
        }
    }

    // ───────────── Base64 / URL ─────────────

    static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string ToBase64(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Decodes Base64 into UTF-8 text. Accepts whitespace, the URL-safe alphabet and missing padding.</summary>
    public static string FromBase64(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length + 3);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }
        var trimmed = sb.ToString().TrimEnd('=');
        if (trimmed.Length % 4 == 1) throw new FormatException("Некорректная строка Base64");
        var padded = trimmed + new string('=', (4 - trimmed.Length % 4) % 4);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Некорректная строка Base64", ex);
        }
        try
        {
            return s_strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("Base64 раскодирован, но внутри не текст", ex);
        }
    }

    public static string UrlEncode(string? text) => string.IsNullOrEmpty(text) ? string.Empty : Uri.EscapeDataString(text);

    /// <summary>Decodes %XX sequences and '+' as space; malformed sequences are left as is.</summary>
    public static string UrlDecode(string? text) => string.IsNullOrEmpty(text) ? string.Empty : WebUtility.UrlDecode(text);

    // ───────────── Stats ─────────────

    /// <summary>Characters, counting a CRLF line break as one.</summary>
    public static int CountChars(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var n = 0;
        foreach (var c in text) if (c != '\r') n++;
        return n;
    }

    public static int CountCharsNoSpaces(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var n = 0;
        foreach (var c in text) if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    /// <summary>Whitespace-separated tokens that contain at least one letter or digit (a lone "—" is not a word).</summary>
    public static int CountWords(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int words = 0;
        bool inToken = false, tokenHasWordChar = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (inToken && tokenHasWordChar) words++;
                inToken = tokenHasWordChar = false;
            }
            else
            {
                inToken = true;
                tokenHasWordChar |= char.IsLetterOrDigit(c);
            }
        }
        if (inToken && tokenHasWordChar) words++;
        return words;
    }

    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lines++;
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) lines++;
        }
        return lines;
    }
}
