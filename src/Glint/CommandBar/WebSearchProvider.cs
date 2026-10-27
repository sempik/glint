using Glint.Core;

namespace Glint.CommandBar;

/// <summary>Always-available last row: search the query on the web.</summary>
public sealed class WebSearchProvider : ICommandProvider
{
    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length == 0 || q.StartsWith('>')) return Array.Empty<CommandResult>();
        var url = "https://www.google.com/search?q=" + Uri.EscapeDataString(q);
        return new[]
        {
            new CommandResult
            {
                Title = $"Искать в браузере: «{q}»",
                Subtitle = "Google",
                Hint = "Браузер",
                Glyph = Glyphs.Globe,
                Execute = () => ShellRun.Open(url),
            },
        };
    }
}
