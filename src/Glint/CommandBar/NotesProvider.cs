using System.Text.RegularExpressions;
using Glint.Core;
using Glint.Pages;
using Glint.Pill;
using Glint.Services;

namespace Glint.CommandBar;

/// <summary>
/// "заметка купить хлеб" / "note buy milk" saves a quick note; any other query of three or more letters also
/// finds notes by title and text and opens them in the Заметки tab.
/// </summary>
public sealed class NotesProvider : ICommandProvider
{
    /// <summary>Create: only the "заметка ..." row, placed early. Search: found notes, placed after apps and files.</summary>
    public enum Mode { Create, Search }

    static readonly Regex Create = new(@"^(?:заметка|запиши|записать|note)(?!\p{L})[\s:,-]*(?<text>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly Mode _mode;

    public NotesProvider(Mode mode) => _mode = mode;

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var q = query.Trim();
        var create = Create.Match(q);
        if (create.Success != (_mode == Mode.Create)) return Array.Empty<CommandResult>();
        if (create.Success)
        {
            var text = create.Groups["text"].Value.Trim();
            if (text.Length == 0)
            {
                return new[]
                {
                    new CommandResult
                    {
                        Title = "Новая заметка",
                        Subtitle = "Допишите текст, чтобы сохранить сразу, или Enter — открыть «Заметки»",
                        Hint = "Заметка",
                        Glyph = Glyphs.Note,
                        Score = 900,
                        Execute = () => (AppServices.Notifier as PillWindow)?.ShowNotes(),
                    },
                };
            }
            return new[]
            {
                new CommandResult
                {
                    Title = $"Сохранить заметку: {text}",
                    Subtitle = "Enter — сохранить в «Заметки»",
                    Hint = "Заметка",
                    Glyph = Glyphs.Note,
                    Score = 900,
                    Execute = () => Save(text),
                },
            };
        }

        if (q.Length < 3 || q.StartsWith('>')) return Array.Empty<CommandResult>();
        ct.ThrowIfCancellationRequested();
        return NoteStore.Shared.List(null, q).Take(3).Select(n => new CommandResult
        {
            Title = n.Title.Length > 0 ? n.Title : "Новая заметка",
            Subtitle = Snippet(n, q),
            Hint = "Заметка",
            Glyph = Glyphs.Note,
            Score = 300,
            Execute = () => (AppServices.Notifier as PillWindow)?.ShowNote(n.Id),
        }).ToList();
    }

    /// <summary>The line around the first match, so it's clear why the note came up.</summary>
    static string Snippet(NoteMeta n, string q)
    {
        var word = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? q;
        var at = n.Text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0) return $"Изменено {n.Modified:dd.MM HH:mm}";
        var start = Math.Max(0, at - 30);
        var snippet = n.Text.Substring(start, Math.Min(90, n.Text.Length - start)).Replace('\n', ' ').Trim();
        return (start > 0 ? "…" : "") + snippet;
    }

    /// <summary>UI thread (builds the RTF with a FlowDocument).</summary>
    static void Save(string text)
    {
        try
        {
            var meta = NoteStore.Shared.Create(null, NotesDocument.FromPlainText(text), text);
            AppServices.Notifier.FlashClickable("Заметка сохранена", $"{meta.Title} · нажмите, чтобы открыть", Glyphs.Note,
                () => (AppServices.Notifier as PillWindow)?.ShowNote(meta.Id), TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier.Flash("Не удалось сохранить заметку", text, Glyphs.Info);
        }
    }
}
