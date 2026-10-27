using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glint.Services.Agents;

/// <summary>
/// What the hook process ("Glint.exe --agent-hook claude") and the running Glint say to each other over the pipe,
/// and how a choice made in the pill becomes the JSON an agent expects on stdout. Shared by both sides, so it must
/// not touch WPF: the hook process never loads it.
/// </summary>
/// <remarks>
/// One connection per hook call. The hook sends one line: {"v":1,"source":"claude","wait":true,"t":…,"payload":{…}}
/// where payload is the agent's hook JSON as it came on stdin. Glint answers with one line: "{}" right away for
/// status events, or {"decision":"allow"|"allow_always"|"deny"|"answer"|"pass", …} once the user chose.
/// </remarks>
public static class AgentProtocol
{
    /// <summary>First command-line argument of the hook mode.</summary>
    public const string Flag = "--agent-hook";

    public const string Claude = "claude", Codex = "codex", CodexNotify = "codex-notify";

    /// <summary>A request never waits longer than this, whatever the setting; the agents' hook timeout is a bit longer.</summary>
    public const int MaxWaitSeconds = 600;
    public const int HookTimeoutSeconds = MaxWaitSeconds + 30;

    /// <summary>Lines longer than this are refused; a Write request can carry a whole file, so it is generous.</summary>
    public const int MaxLineBytes = 16 * 1024 * 1024;

    public const string Allow = "allow", AllowAlways = "allow_always", Deny = "deny", Answer = "answer", Pass = "pass";

    static string? s_pipeName;

    /// <summary>
    /// Per Windows user: pipe names are machine-wide, and another account's Glint must neither collide nor listen.
    /// The ACL (server) and CurrentUserOnly (client) enforce it; the SID in the name just keeps users apart.
    /// </summary>
    public static string PipeName
    {
        get
        {
            if (s_pipeName is not null) return s_pipeName;
            string sid;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value ?? Environment.UserName;
            }
            catch (Exception)
            {
                sid = Environment.UserName;
            }
            return s_pipeName = "Glint.Agents." + sid;
        }
    }

    public static readonly JsonSerializerOptions Compact = new()
    {
        // Russian text and paths stay readable in the agent's log instead of \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? Str(JsonNode? node, string name)
    {
        try
        {
            return node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string EventName(string source, JsonObject payload) =>
        source == CodexNotify ? "Notify:" + (Str(payload, "type") ?? "") : Str(payload, "hook_event_name") ?? "";

    /// <summary>
    /// Whether the agent waits for the user's choice on this call: a permission request (both agents) or Claude's
    /// AskUserQuestion, which a PreToolUse hook answers by filling in "answers". Everything else is status only.
    /// </summary>
    public static bool NeedsDecision(string source, JsonObject payload)
    {
        var name = EventName(source, payload);
        if (name == "PermissionRequest") return source is Claude or Codex;
        return source == Claude && name == "PreToolUse" && Str(payload, "tool_name") == "AskUserQuestion";
    }

    /// <summary>
    /// The JSON to print on the hook's stdout for the user's choice, or null to print nothing, which leaves the
    /// agent to ask in its terminal as if Glint weren't there. Follows the Claude Code and Codex hook formats.
    /// </summary>
    public static string? BuildOutput(string source, JsonObject payload, JsonObject reply)
    {
        var decision = Str(reply, "decision");
        var name = EventName(source, payload);

        if (name == "PermissionRequest" && decision is Allow or AllowAlways or Deny)
        {
            var d = new JsonObject { ["behavior"] = decision == Deny ? "deny" : "allow" };
            if (decision == Deny)
            {
                d["message"] = Str(reply, "message") ?? "Пользователь запретил это действие в Glint.";
            }
            else if (decision == AllowAlways && source == Claude && payload["permission_suggestions"] is JsonArray suggestions && suggestions.Count > 0)
            {
                // The rules Claude Code itself offered under "don't ask again", applied as given.
                d["updatedPermissions"] = suggestions.DeepClone();
            }
            return Wrap("PermissionRequest", new JsonObject { ["decision"] = d });
        }

        if (name == "PreToolUse" && source == Claude && decision == Answer && reply["answers"] is JsonObject answers
            && payload["tool_input"] is JsonObject input)
        {
            // AskUserQuestion takes the answers inside its own input, keyed by the question text.
            var updated = (JsonObject)input.DeepClone();
            updated["answers"] = answers.DeepClone();
            return Wrap("PreToolUse", new JsonObject { ["permissionDecision"] = "allow", ["updatedInput"] = updated });
        }

        if (name == "PreToolUse" && source == Claude && decision == Deny)
        {
            return Wrap("PreToolUse", new JsonObject
            {
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = Str(reply, "message") ?? "Пользователь отклонил вопрос в Glint.",
            });
        }
        return null;
    }

    static string Wrap(string eventName, JsonObject body)
    {
        // hookEventName first, as in the documented examples.
        var output = new JsonObject { ["hookEventName"] = eventName };
        foreach (var (key, value) in body.ToList())
        {
            body.Remove(key);
            output[key] = value;
        }
        return new JsonObject { ["hookSpecificOutput"] = output }.ToJsonString(Compact);
    }

    // ───────────── Pipe I/O ─────────────

    /// <summary>Reads one '\n'-terminated UTF-8 line; null when the other side closed first.</summary>
    public static async Task<string?> ReadLineAsync(PipeStream pipe, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var n = await pipe.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) return buffer.Length > 0 ? Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length) : null;
            var newline = Array.IndexOf(chunk, (byte)'\n', 0, n);
            buffer.Write(chunk, 0, newline >= 0 ? newline : n);
            if (buffer.Length > MaxLineBytes) throw new InvalidDataException("Line too long");
            if (newline >= 0) return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    public static async Task WriteLineAsync(PipeStream pipe, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await pipe.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }
}
