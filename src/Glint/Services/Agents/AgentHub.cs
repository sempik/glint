using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services.Indicators;

namespace Glint.Services.Agents;

/// <summary>
/// What the coding agents are doing right now: one <see cref="AgentSession"/> per running conversation, plus the
/// permission requests and questions waiting for the user. Fed by <see cref="AgentPipeServer"/>; everything here
/// runs on the UI thread. Nothing polls: sessions change only when a hook reports, a terminal closes (a process
/// exit event) or a "готово" grows old (one one-shot timer).
/// </summary>
public sealed class AgentHub : IDisposable
{
    public static AgentHub Shared { get; } = new();

    public enum CloseReason { Withdrawn, Timeout }

    /// <summary>More open requests than this are handed to the terminal at once; nobody answers twenty in a pill.</summary>
    const int MaxOpenRequests = 8;
    /// <summary>A session nobody heard from in this long (and whose process can't be watched) is dropped.</summary>
    static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(8);
    /// <summary>Codex can report a finished turn twice (notify and the Stop hook); one flash is enough.</summary>
    static readonly TimeSpan SameDoneWindow = TimeSpan.FromSeconds(8);

    const string RequestChipId = "agents.request";

    readonly Dictionary<string, AgentSession> _sessions = new();
    readonly List<AgentRequest> _requests = new();
    readonly DispatcherTimer _fade = new();
    AgentPipeServer? _server;
    bool _started;

    AgentHub()
    {
        _fade.Tick += (_, _) =>
        {
            _fade.Stop();
            Publish();
        };
    }

    /// <summary>Raised on the UI thread whenever a session or request changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Most urgent first (waiting, working, error, done, idle), then most recently active.</summary>
    public IReadOnlyList<AgentSession> Sessions { get; private set; } = Array.Empty<AgentSession>();

    /// <summary>Open requests, oldest first: the order the pill asks them in.</summary>
    public IReadOnlyList<AgentRequest> Requests { get; private set; } = Array.Empty<AgentRequest>();

    /// <summary>True while the pipe server runs (Настройки → Агенты → «Принимать события агентов»).</summary>
    public bool IsListening => _server is not null;

    static AgentSettings S => AppServices.Settings.Current.Agents;

    public void Start()
    {
        if (_started) return;
        _started = true;
        Apply();
        AppServices.Settings.Changed += (_, _) => Apply();
    }

    void Apply()
    {
        var want = S.Enabled;
        if (want == (_server is not null))
        {
            // AskInPill turned off: what is waiting goes back to the terminals.
            if (want && !S.AskInPill) foreach (var r in _requests.ToList()) Resolve(r, AgentProtocol.Pass, AgentState.Waiting);
            Publish();
            return;
        }
        if (want)
        {
            _server = new AgentPipeServer(this, Application.Current.Dispatcher);
            _server.Start();
        }
        else
        {
            _server?.Dispose();
            _server = null;
            foreach (var r in _requests.ToList()) Resolve(r, AgentProtocol.Pass, AgentState.Waiting);
            foreach (var s in _sessions.Values) Unwatch(s);
            _sessions.Clear();
        }
        Publish();
    }

    // ───────────── Sessions ─────────────

    /// <summary>"claude:&lt;session id&gt;"; Codex's notify calls it a thread id. Falls back to the folder.</summary>
    public static string? SessionKey(string source, JsonObject payload)
    {
        var agent = source == AgentProtocol.CodexNotify ? AgentProtocol.Codex : source;
        var id = source == AgentProtocol.CodexNotify
            ? AgentProtocol.Str(payload, "thread-id") ?? AgentProtocol.Str(payload, "thread_id") ?? AgentProtocol.Str(payload, "session_id")
            : AgentProtocol.Str(payload, "session_id");
        if (string.IsNullOrEmpty(id))
        {
            var cwd = AgentProtocol.Str(payload, "cwd");
            if (string.IsNullOrEmpty(cwd)) return null;
            id = "cwd:" + cwd.TrimEnd('\\', '/').ToLowerInvariant();
        }
        return agent + ":" + id;
    }

    AgentSession? Find(string source, JsonObject payload, bool create, AgentProcessInfo? process)
    {
        var key = SessionKey(source, payload);
        if (key is null) return null;
        if (!_sessions.TryGetValue(key, out var session))
        {
            var cwd = AgentProtocol.Str(payload, "cwd") ?? "";
            // Codex's notify may name the conversation differently from its hooks: the same folder is the same session.
            if (source == AgentProtocol.CodexNotify && cwd.Length > 0)
            {
                session = _sessions.Values
                    .Where(s => s.Source == AgentProtocol.Codex && string.Equals(s.Cwd.TrimEnd('\\', '/'), cwd.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.LastActivity)
                    .FirstOrDefault();
            }
            if (session is null)
            {
                if (!create) return null;
                session = new AgentSession
                {
                    Key = key,
                    Source = source == AgentProtocol.CodexNotify ? AgentProtocol.Codex : source,
                    SessionId = key[(key.IndexOf(':') + 1)..],
                    Cwd = cwd,
                };
                _sessions[key] = session;
            }
        }
        var newCwd = AgentProtocol.Str(payload, "cwd");
        if (!string.IsNullOrEmpty(newCwd)) session.Cwd = newCwd;
        if (process is not null) Watch(session, process);
        return session;
    }

    /// <summary>Remembers the agent's terminal and removes the session when the agent process exits (an event, no polling).</summary>
    void Watch(AgentSession session, AgentProcessInfo process)
    {
        if (process.Window != IntPtr.Zero) session.Window = process.Window;
        if (process.AgentPid <= 0 || session.AgentPid == process.AgentPid) return;
        Unwatch(session);
        session.AgentPid = process.AgentPid;
        try
        {
            var p = Process.GetProcessById(process.AgentPid);
            p.EnableRaisingEvents = true;
            var key = session.Key;
            p.Exited += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => Forget(key));
            session.Watched = p;
        }
        catch (Exception)
        {
            // Gone already or not ours to watch; the session then ages out instead.
        }
    }

    static void Unwatch(AgentSession session)
    {
        session.Watched?.Dispose();
        session.Watched = null;
    }

    /// <summary>Drops a session (terminal closed, SessionEnd, the × in the Агенты tab) and withdraws its requests.</summary>
    public void Forget(string key)
    {
        if (!_sessions.Remove(key, out var session)) return;
        Unwatch(session);
        // A resumed conversation keeps its id but runs in another terminal: look its process up afresh.
        _server?.ForgetProcess(key);
        foreach (var r in _requests.Where(r => r.Session == session).ToList()) Resolve(r, AgentProtocol.Pass, AgentState.Idle, publish: false);
        Publish();
    }

    /// <summary>A status event (prompt sent, tool done, finished, waiting, closed). Called by the pipe server.</summary>
    internal void OnStatus(string source, JsonObject payload, long t, AgentProcessInfo? process)
    {
        var name = AgentProtocol.EventName(source, payload);
        if (name == "SessionEnd")
        {
            if (SessionKey(source, payload) is { } endKey) Forget(endKey);
            return;
        }
        // Subagents finishing say nothing about the main conversation.
        if (name is "SubagentStop" or "SubagentStart" or "PreCompact" or "PostCompact") return;

        var session = Find(source, payload, create: true, process);
        if (session is null) return;
        // Async hooks may overtake each other; the start time says which came first.
        if (t > 0 && t < session.LastEventT) return;
        if (t > 0) session.LastEventT = t;
        var waitingHere = _requests.Any(r => r.Session == session);

        switch (name)
        {
            case "SessionStart":
                session.SetState(AgentState.Idle, null);
                session.Detail = null;
                break;
            case "UserPromptSubmit":
                session.SetState(AgentState.Working, AgentText.OneLine(AgentProtocol.Str(payload, "prompt")));
                break;
            case "PreToolUse" or "PostToolUse":
                // A request still open keeps the session waiting; a parallel tool finishing doesn't change that.
                if (waitingHere) break;
                var tool = AgentProtocol.Str(payload, "tool_name") ?? "";
                session.SetState(AgentState.Working, ToolLine(tool, payload["tool_input"] as JsonObject, session.Cwd));
                break;
            case "Notification":
                OnNotification(session, payload, waitingHere);
                break;
            case "Stop" or "Notify:agent-turn-complete":
                var answer = AgentProtocol.Str(payload, "last_assistant_message") ?? AgentProtocol.Str(payload, "last-assistant-message");
                var repeat = session.State == AgentState.Done && DateTime.Now - session.StateSince < SameDoneWindow;
                session.SetState(AgentState.Done, AgentText.OneLine(answer) ?? (repeat ? null : "Ответ готов"));
                if (!repeat && S.FlashOnDone) FlashFor(session, $"Готово · {session.Project}", AgentText.OneLine(answer, 70) ?? session.AgentName, Glyphs.Check);
                break;
            case "StopFailure":
                var error = AgentText.Error(AgentProtocol.Str(payload, "error"), AgentProtocol.Str(payload, "error_details"));
                session.SetState(AgentState.Error, error);
                if (S.FlashOnDone) FlashFor(session, $"Ошибка · {session.Project}", $"{session.AgentName}: {error}", Glyphs.Info);
                break;
            case "Notify:approval-requested":
                if (waitingHere) break;
                session.SetState(AgentState.Waiting, "Ждёт разрешения в терминале");
                if (S.FlashOnWaiting) FlashFor(session, $"Ждёт вас · {session.Project}", $"{session.AgentName}: нужно разрешение в терминале", Glyphs.Bell);
                break;
            default:
                // Unknown events still show the session is alive.
                session.LastActivity = DateTime.Now;
                break;
        }
        Publish();
    }

    void OnNotification(AgentSession session, JsonObject payload, bool waitingHere)
    {
        var type = AgentProtocol.Str(payload, "notification_type");
        // Older versions send no type; the message then says what it is about.
        var message = AgentProtocol.Str(payload, "message") ?? "";
        type ??= message.Contains("permission", StringComparison.OrdinalIgnoreCase) ? "permission_prompt" : "agent_needs_input";
        if (type == "idle_prompt")
        {
            // Back at its prompt with nothing to do. After a Stop that changes nothing; after an interrupt (Esc, "No" in
            // its own dialog), which sends no Stop, it ends the "работает" / "ждёт вас" left behind.
            if (!waitingHere && session.State is AgentState.Working or AgentState.Waiting) session.SetState(AgentState.Idle, "Прервано в терминале");
            return;
        }
        string? text = type switch
        {
            "permission_prompt" => "нужно разрешение в терминале",
            "elicitation_dialog" or "elicitation_url_dialog" => "ждёт ответа в терминале",
            "agent_needs_input" => "ждёт вашего ответа",
            _ => null,
        };
        // The pill is asking already, or nothing worth waking the user for (idle reminders, sign-in done).
        if (text is null || waitingHere) return;
        session.SetState(AgentState.Waiting, char.ToUpper(text[0]) + text[1..]);
        // Right after a request timed out to the terminal the user has been told already.
        if (DateTime.Now - session.LastPassAt < TimeSpan.FromSeconds(15)) return;
        if (S.FlashOnWaiting) FlashFor(session, $"Ждёт вас · {session.Project}", $"{session.AgentName}: {text}", Glyphs.Bell);
    }

    static string ToolLine(string tool, JsonObject? input, string cwd)
    {
        var preview = AgentText.OneLine(AgentText.Preview(tool, input, cwd), 80);
        return preview is null || preview == tool ? AgentText.Action(tool) : $"{AgentText.Action(tool)}: {preview}";
    }

    /// <summary>
    /// A clickable flash that brings the agent's terminal forward. Skipped while that terminal is in front: the
    /// user is looking at it already.
    /// </summary>
    static void FlashFor(AgentSession session, string title, string subtitle, string glyph)
    {
        if (AgentProcesses.IsForeground(session.Window)) return;
        var window = session.Window;
        AppServices.Notifier.FlashClickable(title, subtitle, glyph, () => AgentProcesses.Focus(window), TimeSpan.FromSeconds(6));
    }

    // ───────────── Requests ─────────────

    /// <summary>
    /// A permission request or a question the agent waits on. Null means "don't ask here": the hook then prints
    /// nothing and the agent asks in its terminal as usual.
    /// </summary>
    internal AgentRequest? OpenRequest(string source, JsonObject payload, AgentProcessInfo? process)
    {
        var session = Find(source, payload, create: true, process);
        if (session is null) return null;
        if (!S.AskInPill || _requests.Count >= MaxOpenRequests) return Passed(session);
        // Whoever looks at the terminal answers there; the pill would only hold the dialog back.
        if (S.SkipWhenTerminalActive && AgentProcesses.IsForeground(session.Window)) return Passed(session);

        var tool = AgentProtocol.Str(payload, "tool_name") ?? "";
        var input = payload["tool_input"] as JsonObject;
        var seconds = Math.Clamp(S.AskTimeoutSeconds, 10, AgentProtocol.MaxWaitSeconds);
        AgentRequest request;
        if (AgentProtocol.EventName(source, payload) == "PreToolUse")
        {
            var questions = ParseQuestions(input);
            if (questions.Count == 0) return Passed(session);
            request = new AgentRequest
            {
                Session = session,
                Kind = AgentRequestKind.Question,
                Tool = tool,
                Action = questions.Count == 1 ? "Спрашивает" : $"Спрашивает ({questions.Count} {AgentText.Plural(questions.Count, "вопрос", "вопроса", "вопросов")})",
                Preview = questions[0].Text,
                Questions = questions,
                Deadline = DateTime.Now.AddSeconds(seconds),
            };
        }
        else
        {
            request = new AgentRequest
            {
                Session = session,
                Kind = AgentRequestKind.Permission,
                Tool = tool,
                Action = AgentText.Action(tool),
                Preview = AgentText.RequestPreview(tool, input, session.Cwd, out var cut),
                PreviewCut = cut,
                // Codex has no "don't ask again" in its hook answer, so only Claude gets the button.
                AlwaysRule = source == AgentProtocol.Claude ? AgentText.DescribeSuggestions(payload["permission_suggestions"] as JsonArray) : null,
                Deadline = DateTime.Now.AddSeconds(seconds),
            };
        }
        _requests.Add(request);
        session.SetState(AgentState.Waiting, request.Kind == AgentRequestKind.Question ? AgentText.OneLine(request.Preview) : request.Action);
        Publish();
        return request;
    }

    AgentRequest? Passed(AgentSession session)
    {
        session.SetState(AgentState.Waiting, "Ждёт ответа в терминале");
        session.LastPassAt = DateTime.Now;
        Publish();
        return null;
    }

    static List<AgentQuestion> ParseQuestions(JsonObject? input)
    {
        var list = new List<AgentQuestion>();
        if (input?["questions"] is not JsonArray questions) return list;
        foreach (var q in questions.OfType<JsonObject>())
        {
            var text = AgentProtocol.Str(q, "question");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var options = (q["options"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(o => new AgentOption(AgentProtocol.Str(o, "label") ?? "", AgentProtocol.Str(o, "description")))
                .Where(o => o.Label.Length > 0)
                .ToList();
            // A question without choices needs typing; that is the terminal's job.
            if (options.Count == 0) return new List<AgentQuestion>();
            var multi = q["multiSelect"] is JsonValue m && m.TryGetValue<bool>(out var b) && b;
            list.Add(new AgentQuestion(AgentProtocol.Str(q, "header") ?? "", text, options, multi));
        }
        return list;
    }

    /// <summary>The pipe server gave up on a request: the hook died (withdrawn) or the wait ran out.</summary>
    internal void CloseRequest(AgentRequest request, CloseReason reason)
    {
        if (!request.IsOpen) return;
        Resolve(request, AgentProtocol.Pass, AgentState.Waiting, publish: false);
        if (reason == CloseReason.Timeout)
        {
            request.Session.SetState(AgentState.Waiting, "Ждёт ответа в терминале");
            request.Session.LastPassAt = DateTime.Now;
            if (S.FlashOnWaiting)
                FlashFor(request.Session, $"Ждёт вас · {request.Session.Project}", $"{request.Session.AgentName}: спросит в терминале", Glyphs.Bell);
        }
        else if (_requests.All(r => r.Session != request.Session))
        {
            // Cancelled in the terminal (Esc, Ctrl+C): the turn is over and no Stop follows. If it was the agent's own
            // timeout instead, its dialog sends a Notification that makes it "ждёт вас" again.
            request.Session.SetState(AgentState.Idle, "Прервано в терминале");
        }
        Publish();
    }

    public void Allow(AgentRequest request, bool always = false) =>
        Resolve(request, always && request.AlwaysRule is not null ? AgentProtocol.AllowAlways : AgentProtocol.Allow, AgentState.Working);

    public void Deny(AgentRequest request) => Resolve(request, AgentProtocol.Deny, AgentState.Working);

    /// <summary>«В терминале»: the agent shows its own dialog right away.</summary>
    public void PassToTerminal(AgentRequest request)
    {
        request.Session.LastPassAt = DateTime.Now;
        Resolve(request, AgentProtocol.Pass, AgentState.Waiting, publish: false);
        if (_requests.All(r => r.Session != request.Session)) request.Session.Detail = "Ждёт ответа в терминале";
        Publish();
    }

    /// <summary>Answers to a question request, keyed by question text; several choices are comma-joined.</summary>
    public void Answer(AgentRequest request, IReadOnlyDictionary<string, string> answers)
    {
        var obj = new JsonObject();
        foreach (var (question, answer) in answers) obj[question] = answer;
        Resolve(request, new JsonObject { ["decision"] = AgentProtocol.Answer, ["answers"] = obj }, AgentState.Working);
    }

    void Resolve(AgentRequest request, string decision, AgentState next, bool publish = true) =>
        Resolve(request, new JsonObject { ["decision"] = decision }, next, publish);

    void Resolve(AgentRequest request, JsonObject reply, AgentState next, bool publish = true)
    {
        if (!request.IsOpen) return;
        request.IsOpen = false;
        _requests.Remove(request);
        request.Reply.TrySetResult(reply);
        if (_requests.All(r => r.Session != request.Session)) request.Session.SetState(next, next == AgentState.Working ? "Продолжает" : null);
        if (publish) Publish();
    }

    // ───────────── Publishing ─────────────

    void Publish()
    {
        // Sessions nobody can watch (the process lookup failed) age out.
        foreach (var stale in _sessions.Values.Where(s => s.Watched is null && DateTime.Now - s.LastActivity > ForgetAfter && _requests.All(r => r.Session != s)).ToList())
            _sessions.Remove(stale.Key);

        Sessions = _sessions.Values.OrderBy(s => s.Urgency).ThenByDescending(s => s.LastActivity).ToArray();
        Requests = _requests.ToArray();
        ScheduleFade();
        UpdateRequestChip();
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    /// <summary>"Готово", "ошибка" and a silent "работает" fade back to idle after a few minutes; one timer aimed at the next one.</summary>
    void ScheduleFade()
    {
        _fade.Stop();
        var next = _sessions.Values
            .Where(s => s.Shown != AgentState.Idle)
            .Select(s => s.FadesAt ?? DateTime.MaxValue)
            .DefaultIfEmpty(DateTime.MaxValue)
            .Min();
        if (next == DateTime.MaxValue) return;
        _fade.Interval = TimeSpan.FromSeconds(Math.Max(1, (next - DateTime.Now).TotalSeconds + 0.5));
        _fade.Start();
    }

    /// <summary>While something waits, the panel header carries a chip, so an open panel doesn't hide the request.</summary>
    void UpdateRequestChip()
    {
        if (_requests.Count == 0)
        {
            IndicatorHub.Shared.Remove(RequestChipId);
            return;
        }
        var first = _requests[0];
        IndicatorHub.Shared.Set(new Indicator(RequestChipId, Glyphs.Agent, "WarningBrush",
            $"{first.Session.AgentName} · {first.Session.Project}: {char.ToLower(first.Action[0])}{first.Action[1..]}")
        {
            Label = _requests.Count == 1 ? "Ждёт" : $"Ждут {_requests.Count}",
            ChipOnly = true,
            Order = -5,
            Click = () => (AppServices.Notifier as Glint.Pill.PillWindow)?.ShowAgents(),
        });
    }

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
        foreach (var r in _requests.ToList())
        {
            r.IsOpen = false;
            r.Reply.TrySetResult(new JsonObject { ["decision"] = AgentProtocol.Pass });
        }
        _requests.Clear();
        foreach (var s in _sessions.Values) Unwatch(s);
    }
}
