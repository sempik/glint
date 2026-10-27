using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Glint.Services.Agents;

public enum AgentState { Idle, Working, Waiting, Done, Error }

/// <summary>One running agent conversation (a Claude Code or Codex session in some terminal). UI thread only.</summary>
public sealed class AgentSession
{
    /// <summary>"Готово" and "ошибка" count as news this long; after that the session just sits in the list.</summary>
    public static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(3);
    /// <summary>
    /// "Работает" with no event for this long counts as idle: an interrupted turn (Esc, Ctrl+C) sends no Stop, and the
    /// chip must not breathe for hours over an agent that sits at its prompt. Long enough for a slow build or test run.
    /// </summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(10);

    public required string Key { get; init; }
    public required string Source { get; init; }
    public required string SessionId { get; init; }
    public string Cwd { get; set; } = "";
    public AgentState State { get; set; } = AgentState.Idle;
    public DateTime StateSince { get; set; } = DateTime.Now;
    /// <summary>What it is doing or said last: the prompt, the request, the first line of the answer.</summary>
    public string? Detail { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.Now;
    /// <summary>Start time of the last applied status hook (unix ms), to drop async hooks that arrive out of order.</summary>
    public long LastEventT { get; set; }
    public DateTime LastPassAt { get; set; } = DateTime.MinValue;
    public IntPtr Window { get; set; }
    public int AgentPid { get; set; }
    internal Process? Watched { get; set; }

    public string AgentName => Source == AgentProtocol.Codex ? "Codex" : "Claude Code";

    /// <summary>Folder name of the working directory: what tells two sessions apart.</summary>
    public string Project
    {
        get
        {
            var trimmed = Cwd.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? (string.IsNullOrEmpty(trimmed) ? AgentName : trimmed) : name;
        }
    }

    /// <summary>The state as the pill shows it: old news (done, error) and long silence while "working" fade back to idle.</summary>
    public AgentState Shown => FadesAt is { } at && DateTime.Now > at ? AgentState.Idle : State;

    /// <summary>When the shown state falls back to idle by itself; null if it doesn't.</summary>
    public DateTime? FadesAt => State switch
    {
        AgentState.Done or AgentState.Error => StateSince + RecentFor,
        AgentState.Working => LastActivity + QuietFor,
        _ => null,
    };

    public static string StateText(AgentState state) => state switch
    {
        AgentState.Working => "работает",
        AgentState.Waiting => "ждёт вас",
        AgentState.Done => "готово",
        AgentState.Error => "ошибка",
        _ => "свободен",
    };

    public static string StateBrush(AgentState state) => state switch
    {
        AgentState.Working => "AccentBrush",
        AgentState.Waiting => "WarningBrush",
        AgentState.Done => "SuccessBrush",
        AgentState.Error => "DangerBrush",
        _ => "MutedBrush",
    };

    /// <summary>Lower is more urgent: what the pill shows when several sessions run at once.</summary>
    public int Urgency => Shown switch
    {
        AgentState.Waiting => 0,
        AgentState.Working => 1,
        AgentState.Error => 2,
        AgentState.Done => 3,
        _ => 4,
    };

    public void SetState(AgentState state, string? detail)
    {
        if (state != State) StateSince = DateTime.Now;
        State = state;
        if (detail is not null) Detail = detail;
        LastActivity = DateTime.Now;
    }
}

public enum AgentRequestKind { Permission, Question }

public sealed record AgentOption(string Label, string? Description);

public sealed record AgentQuestion(string Header, string Text, IReadOnlyList<AgentOption> Options, bool MultiSelect);

/// <summary>A tool call waiting for the user's yes / no, or a question waiting for an answer. Created on the UI thread.</summary>
public sealed class AgentRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public required AgentSession Session { get; init; }
    public required AgentRequestKind Kind { get; init; }
    public required string Tool { get; init; }
    /// <summary>"Выполнить команду", "Изменить файл"…</summary>
    public required string Action { get; init; }
    public required string Preview { get; init; }
    /// <summary>The preview is too long to show whole: the card then offers no «Разрешить», only the terminal and «Запретить».</summary>
    public bool PreviewCut { get; init; }
    /// <summary>Set when Claude Code offers "don't ask again": what would be allowed from now on.</summary>
    public string? AlwaysRule { get; init; }
    public IReadOnlyList<AgentQuestion> Questions { get; init; } = Array.Empty<AgentQuestion>();
    public required DateTime Deadline { get; init; }
    public DateTime Created { get; } = DateTime.Now;
    public bool IsOpen { get; internal set; } = true;

    /// <summary>Completed with the reply line for the hook; the pipe server waits on it.</summary>
    internal TaskCompletionSource<JsonObject> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TimeSpan Remaining => Deadline - DateTime.Now is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
}
