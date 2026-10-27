using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Glint.Pages;

/// <summary>
/// FlowDocument helpers for notes: RTF in and out, plain text, and colour cleanup. Text colour is never stored as
/// "white" or "black": RTF bakes the inherited colour into every run, which would turn a note written in the dark
/// theme invisible in the light one, so the theme's own text colours are stripped on load and paste.
/// Checklist items are paragraphs that start with ☐ or ☑ (they survive RTF and read fine in WordPad).
/// </summary>
public static class NotesDocument
{
    // RTF stores sizes in half-points, so these are whole points (10, 15, 12 pt) to come back unchanged.
    public const double BodySize = 40.0 / 3, H1Size = 20, H2Size = 16;
    public const string Unchecked = "☐", Checked = "☑";

    public static FlowDocument Create()
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(2, 2, 2, 8),
            FontSize = BodySize,
            TextAlignment = TextAlignment.Left,
        };
        if (Application.Current?.TryFindResource("UiFont") is FontFamily font) doc.FontFamily = font;
        return doc;
    }

    public static byte[] Save(FlowDocument doc)
    {
        using var ms = new MemoryStream();
        new TextRange(doc.ContentStart, doc.ContentEnd).Save(ms, DataFormats.Rtf);
        return ms.ToArray();
    }

    /// <summary>
    /// Loads RTF. A damaged file falls back to the text inside it rather than an empty note, and returns false:
    /// that text no longer holds the formatting, so the caller keeps the original before it gets saved over.
    /// </summary>
    public static bool Load(FlowDocument doc, byte[] rtf)
    {
        if (rtf.Length == 0) return true;
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        var ok = true;
        try
        {
            using var ms = new MemoryStream(rtf);
            range.Load(ms, DataFormats.Rtf);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            range.Text = TextOf(rtf);
            ok = false;
        }
        Normalize(doc);
        return ok;
    }

    // Groups whose content isn't text: font and colour tables, styles, document info, pictures.
    static readonly HashSet<string> SkippedGroups = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "footer", "themedata", "datastore", "generator",
    };

    /// <summary>
    /// The words of an RTF file the WPF reader refused, without the markup: escapes decoded, tables and pictures
    /// skipped. Not RTF at all (a .txt renamed by hand): the file as UTF-8 text.
    /// </summary>
    static string TextOf(byte[] rtf)
    {
        var s = System.Text.Encoding.Latin1.GetString(rtf);
        if (!s.StartsWith(@"{\rtf", StringComparison.Ordinal)) return System.Text.Encoding.UTF8.GetString(rtf);

        var sb = new System.Text.StringBuilder(s.Length / 4);
        var bytes = new List<byte>();
        System.Text.Encoding ansi = System.Text.Encoding.Latin1;
        // Per group: skipped or not, and how many fallback characters follow a \uN.
        var stack = new Stack<(bool Skip, int Uc)>();
        bool skip = false;
        int uc = 1, pendingSkip = 0;

        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            if (!skip) sb.Append(ansi.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        void Emit(char c)
        {
            if (pendingSkip > 0) { pendingSkip--; return; }
            FlushBytes();
            if (!skip) sb.Append(c);
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '{':
                    FlushBytes();
                    stack.Push((skip, uc));
                    pendingSkip = 0;
                    break;
                case '}':
                    FlushBytes();
                    if (stack.Count > 0) (skip, uc) = stack.Pop();
                    pendingSkip = 0;
                    break;
                case '\r' or '\n':
                    break;
                case '\\' when i + 1 < s.Length:
                {
                    var n = s[i + 1];
                    if (n == '\'' && i + 3 < s.Length && byte.TryParse(s.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                    {
                        i += 3;
                        if (pendingSkip > 0) { pendingSkip--; break; }
                        bytes.Add(b);
                        break;
                    }
                    if (!char.IsAsciiLetter(n))
                    {
                        // Control symbols: \* marks a group to ignore, \\ \{ \} are literal, \~ \_ are spaces and dashes.
                        i++;
                        if (n == '*') skip = true;
                        else if (n is '\\' or '{' or '}') Emit(n);
                        else if (n == '~') Emit(' ');
                        else if (n == '_') Emit('-');
                        break;
                    }
                    // A control word: letters, an optional signed number, an optional space.
                    var j = i + 1;
                    while (j < s.Length && char.IsAsciiLetter(s[j])) j++;
                    var word = s[(i + 1)..j];
                    var k = j;
                    if (k < s.Length && s[k] == '-') k++;
                    while (k < s.Length && char.IsAsciiDigit(s[k])) k++;
                    int? arg = k > j && int.TryParse(s.AsSpan(j, k - j), out var a) ? a : null;
                    if (k < s.Length && s[k] == ' ') k++;
                    i = k - 1;

                    switch (word)
                    {
                        case "par" or "line" or "row": Emit('\n'); break;
                        case "tab" or "cell": Emit('\t'); break;
                        case "uc": uc = Math.Max(0, arg ?? 1); break;
                        case "u" when arg is { } code:
                            Emit((char)(code < 0 ? code + 65536 : code));
                            pendingSkip = uc;
                            break;
                        case "ansicpg" when arg is { } cp:
                            ansi = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(cp) ?? ansi;
                            break;
                        case "bin" when arg is > 0:
                            // Raw binary data: never text.
                            i = Math.Min(s.Length - 1, i + arg.Value);
                            break;
                        default:
                            if (SkippedGroups.Contains(word)) skip = true;
                            break;
                    }
                    break;
                }
                default:
                    Emit(c);
                    break;
            }
        }
        FlushBytes();
        return sb.ToString().Trim();
    }

    /// <summary>One paragraph per line; used for quick notes from the command bar.</summary>
    public static byte[] FromPlainText(string text)
    {
        var doc = Create();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            doc.Blocks.Add(new Paragraph(new Run(line)));
        return Save(doc);
    }

    public static string PlainText(FlowDocument doc) => new TextRange(doc.ContentStart, doc.ContentEnd).Text;

    /// <summary>Clears baked-in theme text colours and backgrounds; deliberate colours (accent, red...) stay.</summary>
    public static void Normalize(FlowDocument doc)
    {
        foreach (var block in doc.Blocks.ToList()) Normalize(block);
    }

    static void Normalize(TextElement element)
    {
        if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush fg && IsThemeInk(fg.Color))
            element.ClearValue(TextElement.ForegroundProperty);
        // The editor never sets backgrounds; pasted ones (white page fills) only hide the text.
        element.ClearValue(TextElement.BackgroundProperty);

        switch (element)
        {
            case Paragraph p:
                foreach (var i in p.Inlines.ToList()) Normalize(i);
                break;
            case Span s:
                foreach (var i in s.Inlines.ToList()) Normalize(i);
                break;
            case List l:
                foreach (var item in l.ListItems.ToList())
                {
                    Normalize(item);
                    foreach (var b in item.Blocks.ToList()) Normalize(b);
                }
                break;
            case Section sec:
                foreach (var b in sec.Blocks.ToList()) Normalize(b);
                break;
            case Table t:
                foreach (var group in t.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var b in cell.Blocks.ToList()) Normalize(b);
                break;
        }
    }

    /// <summary>Drops every colour set on the paragraph and its text, so it inherits the editor's theme colour again.</summary>
    public static void ClearForeground(Paragraph p)
    {
        p.ClearValue(TextElement.ForegroundProperty);
        ClearForeground(p.Inlines);
    }

    static void ClearForeground(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            i.ClearValue(TextElement.ForegroundProperty);
            if (i is Span s) ClearForeground(s.Inlines);
        }
    }

    /// <summary>Black, white, and both palettes' text colours (see ThemeService).</summary>
    static bool IsThemeInk(Color c)
    {
        if (c.A == 0) return true;
        var rgb = (c.R << 16) | (c.G << 8) | c.B;
        return rgb is 0x000000 or 0xFFFFFF or 0xF5F5F7 or 0x17171F;
    }

    /// <summary>Paragraphs touched by the selection, including those inside lists.</summary>
    public static List<Paragraph> ParagraphsIn(FlowDocument doc, TextPointer start, TextPointer end)
    {
        var result = new List<Paragraph>();
        Collect(doc.Blocks, start, end, result);
        return result;
    }

    static void Collect(BlockCollection blocks, TextPointer start, TextPointer end, List<Paragraph> result)
    {
        foreach (var block in blocks)
        {
            // Skip blocks that end before the selection or start after it.
            if (block.ContentEnd.CompareTo(start) < 0 || block.ContentStart.CompareTo(end) > 0) continue;
            switch (block)
            {
                case Paragraph p:
                    result.Add(p);
                    break;
                case List l:
                    foreach (var item in l.ListItems) Collect(item.Blocks, start, end, result);
                    break;
                case Section s:
                    Collect(s.Blocks, start, end, result);
                    break;
            }
        }
    }

    /// <summary>The paragraph's first character as a range, or null for an empty paragraph.</summary>
    public static TextRange? FirstChar(Paragraph p)
    {
        var start = p.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var next = start.GetNextInsertionPosition(LogicalDirection.Forward);
        if (next is null || next.CompareTo(p.ContentEnd) > 0) return null;
        return new TextRange(start, next);
    }

    public static bool IsChecklist(Paragraph p) => FirstChar(p)?.Text is Unchecked or Checked;
}
