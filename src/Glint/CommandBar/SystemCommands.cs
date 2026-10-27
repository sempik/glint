using System.Runtime.InteropServices;
using Glint.Core;

namespace Glint.CommandBar;

/// <summary>Built-in actions plus "> command" which runs in a new cmd window.</summary>
public sealed class SystemCommands : ICommandProvider
{
    sealed record BuiltIn(string Title, string Subtitle, string Glyph, string[] Keywords, Action Run)
    {
        public string Lower { get; } = Title.ToLowerInvariant();
        public string Initials { get; } = Matcher.Initials(Title);
    }

    // Segoe MDL2 "CommandPrompt"; not in Core.Glyphs because only the command bar uses it.
    const string TerminalGlyph = "";

    static readonly BuiltIn[] BuiltIns =
    {
        new("Скриншот области", "Выделить часть экрана", Glyphs.Camera,
            new[] { "screenshot", "скриншот", "снимок", "screen", "snip" }, () => ShellRun.Open("ms-screenclip:")),
        new("Заблокировать компьютер", "То же, что Win+L", Glyphs.Lock,
            new[] { "lock", "блок", "заблокировать", "замок" }, () => Interop.LockWorkStation()),
        new("Настройки Windows", "Параметры системы", Glyphs.Settings,
            new[] { "settings", "параметры", "настройки", "windows" }, () => ShellRun.Open("ms-settings:")),
        new("Корзина", "Открыть корзину", Glyphs.Delete,
            new[] { "recycle", "trash", "корзина", "bin" }, () => ShellRun.Open("explorer.exe", "shell:RecycleBinFolder")),
    };

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.StartsWith('>')) return RunCommand(q[1..].Trim());

        var lower = q.ToLowerInvariant();
        if (lower.Length < 2) return Array.Empty<CommandResult>();

        var list = new List<CommandResult>();
        foreach (var b in BuiltIns)
        {
            var score = Matcher.Score(b.Lower, b.Initials, lower, fuzzy: false);
            if (b.Keywords.Any(k => k.StartsWith(lower, StringComparison.Ordinal))) score = Math.Max(score, 90);
            if (score > 0) list.Add(ToResult(b, score));
        }
        return list.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>Shown when the query is empty.</summary>
    public static IEnumerable<CommandResult> QuickSuggestions() => BuiltIns.Take(3).Select(b => ToResult(b, 0));

    static CommandResult ToResult(BuiltIn b, double score) => new()
    {
        Title = b.Title,
        Subtitle = b.Subtitle,
        Hint = "Команда",
        Glyph = b.Glyph,
        Score = score,
        Execute = b.Run,
    };

    static IEnumerable<CommandResult> RunCommand(string command)
    {
        if (command.Length == 0) return Array.Empty<CommandResult>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            new CommandResult
            {
                Title = $"Выполнить: {command}",
                Subtitle = "В новом окне cmd · Ctrl+Shift+Enter — от администратора",
                Hint = "Консоль",
                Glyph = TerminalGlyph,
                Score = 500,
                // /k keeps the window open so the output stays readable.
                Execute = () => ShellRun.Open("cmd.exe", "/k " + command, workingDir: home),
                // Elevated processes ignore the working directory, so change it inside cmd.
                ExecuteElevated = () => ShellRun.Open("cmd.exe", $"/k cd /d \"{home}\" && {command}", elevated: true),
            },
        };
    }

    static class Interop
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LockWorkStation();
    }
}
