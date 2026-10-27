using System.Diagnostics;
using Glint.Core;

namespace Glint.CommandBar;

/// <summary>One row in the command bar.</summary>
public sealed class CommandResult
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    /// <summary>Right-aligned muted label ("Приложение", "Файл", ...).</summary>
    public string? Hint { get; init; }
    public string Glyph { get; init; } = Glyphs.Search;
    /// <summary>File whose shell icon replaces the glyph once loaded (see <see cref="IconCache"/>).</summary>
    public string? IconPath { get; init; }
    public double Score { get; init; }
    /// <summary>Runs on the UI thread after the bar has hidden itself.</summary>
    public required Action Execute { get; init; }
    /// <summary>File or folder behind the result; enables Ctrl+Enter (show in folder).</summary>
    public string? Path { get; init; }
    /// <summary>Ctrl+Shift+Enter; null when elevation makes no sense for this result.</summary>
    public Action? ExecuteElevated { get; init; }
    /// <summary>Offers "pin to Home" in the row's context menu; uses <see cref="Path"/>.</summary>
    public bool CanPin { get; init; }
}

public interface ICommandProvider
{
    /// <summary>Called on a background thread; must not touch UI objects.</summary>
    IEnumerable<CommandResult> Query(string query, CancellationToken ct);
}

internal static class ShellRun
{
    public static void Open(string target, string? args = null, bool elevated = false, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (args is not null) psi.Arguments = args;
        if (elevated) psi.Verb = "runas";
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Opens Explorer with the item selected.</summary>
    public static void Reveal(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }
}
