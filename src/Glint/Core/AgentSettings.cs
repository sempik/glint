namespace Glint.Core;

/// <summary>
/// AI coding agents (Claude Code, Codex) reporting to the pill (Настройки → Агенты). Everything is local: the
/// agents start "Glint.exe --agent-hook" for their events, and it talks to the running Glint over a named pipe
/// that only this Windows user can open. Off until the user connects an agent.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>Wait times offered in Настройки, seconds.</summary>
    public static readonly int[] AskTimeouts = { 30, 60, 120, 300, 600 };

    /// <summary>Runs the pipe server. «Подключить» turns it on; while off, the hooks find no pipe and exit at once.</summary>
    public bool Enabled { get; set; }
    /// <summary>Permission requests and questions wait for a choice in the pill; off means status only.</summary>
    public bool AskInPill { get; set; } = true;
    /// <summary>A request from an agent whose terminal is in front is left to the terminal: the user is looking at it.</summary>
    public bool SkipWhenTerminalActive { get; set; } = true;
    /// <summary>How long a request waits in the pill before the agent asks in its terminal as usual.</summary>
    public int AskTimeoutSeconds { get; set; } = 120;
    /// <summary>The agent's state (working / waiting / done) next to the clock.</summary>
    public bool ShowInPill { get; set; } = true;
    /// <summary>A flash when an agent finishes its answer.</summary>
    public bool FlashOnDone { get; set; } = true;
    /// <summary>A flash when an agent waits for you in its terminal (input, a dialog Glint can't answer).</summary>
    public bool FlashOnWaiting { get; set; } = true;

    public void Normalize()
    {
        // The hook gives up after 10 minutes whatever the file says (see AgentProtocol.MaxWaitSeconds).
        if (!AskTimeouts.Contains(AskTimeoutSeconds))
            AskTimeoutSeconds = AskTimeouts.OrderBy(t => Math.Abs(t - AskTimeoutSeconds)).First();
    }
}
