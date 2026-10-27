using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Glint.Core;

namespace Glint.Services.Agents;

public enum AgentKind { Claude, Codex }

public enum HookStatus { NotConnected, Connected, Outdated, Unreadable }

/// <summary>What «Подключить» / «Отключить» would change, shown to the user before anything is written.</summary>
public sealed record HookPlan(
    AgentKind Agent,
    bool Connect,
    string FilePath,
    /// <summary>The file as read when planning; applying refuses if it changed since.</summary>
    string? OriginalText,
    string NewText,
    /// <summary>The exact lines added (or removed), for the confirmation.</summary>
    string Snippet,
    /// <summary>Notes for the user: what is kept, what Codex still needs.</summary>
    IReadOnlyList<string> Notes);

/// <summary>
/// Adds Glint's hooks to Claude Code's settings.json and Codex's config.toml, and takes them out again. Only Glint's
/// own entries are touched: every other setting and hook stays as it was, a backup is written next to the file before
/// each change, and connecting twice changes nothing. A Glint entry is recognised by the "--agent-hook" switch.
/// </summary>
public static class AgentInstaller
{
    // Claude Code events and how each is registered. Status events run "async", so the agent never waits for Glint;
    // a permission request and AskUserQuestion wait for the answer, up to the hook timeout.
    static readonly (string Event, string? Matcher, bool Async)[] ClaudeEvents =
    {
        ("SessionStart", null, true),
        ("UserPromptSubmit", null, true),
        ("PreToolUse", "AskUserQuestion", false),
        ("PermissionRequest", null, false),
        ("PostToolUse", null, true),
        // idle_prompt: back at the prompt, the only sign that an interrupted turn (no Stop) is over.
        ("Notification", "permission_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input|idle_prompt", true),
        ("Stop", null, true),
        ("StopFailure", null, true),
        ("SessionEnd", null, false),
    };

    // Codex events: status ones return in a fraction of a second, PostToolUse runs in the background.
    static readonly (string Event, bool Async, int Timeout)[] CodexEvents =
    {
        ("SessionStart", false, 10),
        ("UserPromptSubmit", false, 10),
        ("PermissionRequest", false, AgentProtocol.HookTimeoutSeconds),
        ("PostToolUse", true, 10),
        ("Stop", false, 10),
        ("SessionEnd", false, 3),
    };

    const string HooksBegin = "# >>> Glint agent hooks (added by Glint: Settings > Agents; Disconnect removes this block)";
    const string HooksEnd = "# <<< Glint agent hooks";
    const string NotifyBegin = "# >>> Glint agent notify (added by Glint: Settings > Agents)";
    const string NotifyEnd = "# <<< Glint agent notify";
    const string WaitMessage = "Ждёт ответа в Glint";

    static readonly JsonSerializerOptions Indented = new(AgentProtocol.Compact) { WriteIndented = true };

    // ───────────── Paths ─────────────

    public static string ClaudeSettingsPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(dir, "settings.json");
        }
    }

    public static string CodexConfigPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(dir, "config.toml");
        }
    }

    public static string PathFor(AgentKind agent) => agent == AgentKind.Claude ? ClaudeSettingsPath : CodexConfigPath;

    /// <summary>The Glint.exe the agents should start; null when Glint runs in a way that can't be started directly.</summary>
    public static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            // "dotnet Glint.dll": the host isn't Glint.
            return string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase) ? null : path;
        }
    }

    // ───────────── Status ─────────────

    /// <summary>Whether Glint's hooks are in the agent's config, and for this Glint.exe.</summary>
    public static (HookStatus Status, string? ConnectedExe) GetStatus(AgentKind agent)
    {
        try
        {
            var path = PathFor(agent);
            if (!File.Exists(path)) return (HookStatus.NotConnected, null);
            var text = File.ReadAllText(path);
            return agent == AgentKind.Claude ? ClaudeStatus(text) : CodexStatus(text);
        }
        catch (Exception)
        {
            return (HookStatus.Unreadable, null);
        }
    }

    static (HookStatus, string?) ClaudeStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (HookStatus.NotConnected, null);
        JsonObject root;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject r) return (HookStatus.Unreadable, null);
            root = r;
        }
        catch (JsonException)
        {
            return (HookStatus.Unreadable, null);
        }
        if (root["hooks"] is not JsonObject hooks) return (HookStatus.NotConnected, null);
        string? exe = null;
        // Event → the matcher of its Glint group, so an older matcher shows as "устарели" and gets reconnected.
        var events = new Dictionary<string, string?>();
        foreach (var (name, node) in hooks)
        {
            foreach (var group in (node as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                foreach (var handler in (group["hooks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(IsGlintHandler))
                {
                    exe ??= AgentProtocol.Str(handler, "command");
                    events[name] = AgentProtocol.Str(group, "matcher");
                }
            }
        }
        if (exe is null) return (HookStatus.NotConnected, null);
        var complete = ClaudeEvents.All(e => events.TryGetValue(e.Event, out var matcher) && matcher == e.Matcher) && IsThisExe(exe);
        return (complete ? HookStatus.Connected : HookStatus.Outdated, exe);
    }

    static (HookStatus, string?) CodexStatus(string text)
    {
        var block = FindBlock(text, HooksBegin, HooksEnd);
        if (block is null) return (HookStatus.NotConnected, null);
        var body = text.Substring(block.Value.Start, block.Value.Length);
        var exe = CodexExe(body);
        if (exe is null) return (HookStatus.Outdated, null);
        var complete = CodexEvents.All(e => body.Contains($"[[hooks.{e.Event}]]", StringComparison.Ordinal)) && IsThisExe(exe);
        return (complete ? HookStatus.Connected : HookStatus.Outdated, exe);
    }

    /// <summary>
    /// The program of the first Glint command in the hooks block. The TOML string is read first (literal or basic,
    /// as <see cref="TomlString"/> writes it), then its first word, which <see cref="ShellPath"/> may have quoted.
    /// </summary>
    static string? CodexExe(string body)
    {
        foreach (Match m in Regex.Matches(body, @"(?m)^\s*command\s*=\s*(?:'([^']*)'|""((?:[^""\\]|\\.)*)"")"))
        {
            var value = m.Groups[1].Success ? m.Groups[1].Value : Regex.Replace(m.Groups[2].Value, @"\\(.)", "$1");
            var program = Regex.Match(value, $@"^\s*(?:""([^""]+)""|(\S+))\s+{Regex.Escape(AgentProtocol.Flag)}(\s|$)");
            if (program.Success) return program.Groups[1].Success ? program.Groups[1].Value : program.Groups[2].Value;
        }
        return null;
    }

    /// <summary>Whether a path read from a config file (native, or the shell form written for Codex) is this Glint.exe.</summary>
    public static bool IsThisExe(string? exe)
    {
        if (string.IsNullOrEmpty(exe) || ExePath is not { } current) return false;
        var path = exe.Trim('"');
        return SamePath(path.Replace('/', '\\'), current) || string.Equals(path, ShellPath(current).Trim('"'), StringComparison.OrdinalIgnoreCase);
    }

    static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ───────────── Planning ─────────────

    /// <summary>
    /// Works out the change without writing anything. Throws <see cref="InvalidDataException"/> with a message for
    /// the user when the file can't be changed safely (not valid JSON, a hooks layout Glint can't extend).
    /// </summary>
    public static HookPlan Plan(AgentKind agent, bool connect)
    {
        var exe = ExePath;
        if (connect && exe is null) throw new InvalidDataException("Glint запущен не из Glint.exe, агентам нечего запускать.");
        var path = PathFor(agent);
        string? original = null;
        if (File.Exists(path))
        {
            try
            {
                original = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Не удалось прочитать {path}: {ex.Message}");
            }
        }
        return agent == AgentKind.Claude ? PlanClaude(path, original, connect, exe) : PlanCodex(path, original, connect, exe);
    }

    static HookPlan PlanClaude(string path, string? original, bool connect, string? exe)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(original))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                // Strict: a file with comments would lose them on rewrite, so it isn't touched at all.
                root = JsonNode.Parse(original) as JsonObject ?? throw new InvalidDataException("В settings.json не объект JSON.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"settings.json не читается как JSON (строка {ex.LineNumber + 1}). Исправьте файл и попробуйте снова.");
            }
        }
        if (root["hooks"] is { } existing && existing is not JsonObject)
            throw new InvalidDataException("В settings.json поле «hooks» не объект; Glint не будет его менять.");

        var hooks = root["hooks"] as JsonObject;
        var removed = hooks is null ? 0 : RemoveGlintHandlers(hooks);
        var added = new JsonObject();
        if (connect)
        {
            if (hooks is null)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }
            foreach (var (name, matcher, isAsync) in ClaudeEvents)
            {
                var handler = new JsonObject
                {
                    ["type"] = "command",
                    // Exec form: the agent starts Glint.exe directly, no shell and no quoting.
                    ["command"] = exe,
                    ["args"] = new JsonArray(AgentProtocol.Flag, AgentProtocol.Claude),
                };
                if (isAsync) handler["async"] = true;
                else if (name != "SessionEnd")
                {
                    handler["timeout"] = AgentProtocol.HookTimeoutSeconds;
                    handler["statusMessage"] = WaitMessage;
                }
                var group = new JsonObject();
                if (matcher is not null) group["matcher"] = matcher;
                group["hooks"] = new JsonArray(handler);

                if (hooks[name] is not JsonArray list)
                {
                    if (hooks[name] is not null) throw new InvalidDataException($"В settings.json hooks.{name} не список; Glint не будет его менять.");
                    list = new JsonArray();
                    hooks[name] = list;
                }
                list.Add(group);
                added[name] = new JsonArray(group.DeepClone());
            }
        }
        else if (hooks is not null && hooks.Count == 0)
        {
            // Only Glint's hooks were there; leave no empty object behind.
            root.Remove("hooks");
        }

        var text = root.ToJsonString(Indented);
        if (original is not null && original.EndsWith('\n')) text += Environment.NewLine;
        var notes = new List<string>();
        if (connect)
        {
            notes.Add("Остальные настройки и ваши хуки остаются как есть.");
            if (removed > 0) notes.Add("Прежние записи Glint заменяются новыми.");
            notes.Add("Сработает в новых сессиях Claude Code (уже открытые подхватят после перезапуска).");
        }
        else
        {
            notes.Add($"Убираются только записи Glint ({removed}); остальное не меняется.");
        }
        var snippet = connect
            ? new JsonObject { ["hooks"] = added }.ToJsonString(Indented)
            : $"Удаляются обработчики с «{AgentProtocol.Flag} {AgentProtocol.Claude}» из раздела hooks.";
        return new HookPlan(AgentKind.Claude, connect, path, original, text, snippet, notes);
    }

    static HookPlan PlanCodex(string path, string? original, bool connect, string? exe)
    {
        var text = original ?? "";
        // The file's own line ending, so a round trip leaves it byte for byte as it was.
        var nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
        // Our own blocks come out first; connecting again puts fresh ones back.
        text = RemoveBlock(text, HooksBegin, HooksEnd);
        text = RemoveBlock(text, NotifyBegin, NotifyEnd);
        var notes = new List<string>();
        string snippet;

        if (connect)
        {
            CheckCodexConflicts(text);
            var command = $"{ShellPath(exe!)} {AgentProtocol.Flag} {AgentProtocol.Codex}";
            var hooks = new StringBuilder();
            hooks.AppendLine(HooksBegin);
            foreach (var (name, isAsync, timeout) in CodexEvents)
            {
                hooks.AppendLine($"[[hooks.{name}]]");
                hooks.AppendLine($"[[hooks.{name}.hooks]]");
                hooks.AppendLine("type = \"command\"");
                hooks.AppendLine($"command = {TomlString(command)}");
                hooks.AppendLine($"timeout = {timeout}");
                if (isAsync) hooks.AppendLine("async = true");
                if (name == "PermissionRequest") hooks.AppendLine($"statusMessage = {TomlString(WaitMessage)}");
                hooks.AppendLine();
            }
            hooks.Append(HooksEnd).AppendLine();

            // notify is a single top-level program: only set when the user has none of their own.
            string? notify = null;
            if (HasTopLevelKey(text, "notify"))
            {
                notes.Add("У вас уже задан notify — Glint его не трогает; о готовых ответах сообщит хук Stop.");
            }
            else
            {
                notify = $"{NotifyBegin}{nl}notify = [{TomlString(exe!)}, {TomlString(AgentProtocol.Flag)}, {TomlString(AgentProtocol.CodexNotify)}]{nl}{NotifyEnd}{nl}";
                // Top-level keys must come before the first table, so it goes first in the file.
                text = notify + (text.Length > 0 ? nl : "") + text;
            }
            if (text.Length > 0 && !text.EndsWith('\n')) text += nl;
            if (text.Length > 0) text += nl;
            text += hooks.ToString().Replace("\r\n", "\n").Replace("\n", nl);

            snippet = (notify ?? "") + (notify is null ? "" : nl) + hooks;
            notes.Add("Остальные настройки остаются как есть.");
            notes.Add("Codex запускает новые хуки только после проверки: откройте в Codex /hooks и отметьте хуки Glint как доверенные.");
            if (Regex.IsMatch(text, @"(?m)^\s*hooks\s*=\s*false")) notes.Add("В [features] у вас hooks = false — пока это так, Codex хуки не запускает (останется только notify).");
        }
        else
        {
            snippet = "Удаляются блоки между строками «# >>> Glint agent …» и «# <<< Glint agent …».";
            notes.Add("Остальные настройки не меняются.");
        }
        return new HookPlan(AgentKind.Codex, connect, path, original, text, snippet, notes);
    }

    /// <summary>Inline hook tables (hooks = { … } or Stop = [ … ]) can't be extended with [[hooks.Stop]] entries.</summary>
    static void CheckCodexConflicts(string text)
    {
        if (Regex.IsMatch(text, @"(?m)^\s*hooks\s*=\s*\{"))
            throw new InvalidDataException("В config.toml хуки заданы одной строкой (hooks = { … }); добавьте Glint вручную или перенесите их в [[hooks.…]].");
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']', ' ');
                continue;
            }
            if (section != "hooks") continue;
            var key = line.Split('=')[0].Trim();
            if (CodexEvents.Any(e => e.Event == key) && line.Contains('='))
                throw new InvalidDataException($"В config.toml в [hooks] уже есть «{key} = …»; такой список Glint не может дополнить.");
        }
    }

    static bool HasTopLevelKey(string text, string key)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) return false;
            if (Regex.IsMatch(line, $@"^{Regex.Escape(key)}\s*=")) return true;
        }
        return false;
    }

    // ───────────── Applying ─────────────

    /// <summary>
    /// Writes the planned change after a timestamped backup of the old file. Returns the backup path (null when
    /// there was no file). Throws if the file changed since planning, so nothing the user didn't see is written.
    /// </summary>
    public static string? Apply(HookPlan plan)
    {
        var current = File.Exists(plan.FilePath) ? File.ReadAllText(plan.FilePath) : null;
        if (current != plan.OriginalText)
            throw new InvalidDataException("Файл изменился, пока вы смотрели. Нажмите кнопку ещё раз.");
        if (current == plan.NewText) return null;
        string? backup = null;
        if (current is not null)
        {
            // Never over an older backup: connecting and disconnecting within a second must keep both.
            var stem = $"{plan.FilePath}.glint-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            backup = stem;
            for (var n = 2; File.Exists(backup); n++) backup = $"{stem}-{n}";
            File.Copy(plan.FilePath, backup);
        }
        AtomicFile.WriteAllText(plan.FilePath, plan.NewText);
        return backup;
    }

    // ───────────── JSON helpers ─────────────

    static bool IsGlintHandler(JsonObject handler)
    {
        if (handler["args"] is JsonArray { Count: > 0 } args && args[0] is JsonValue first
            && first.TryGetValue<string>(out var arg) && arg == AgentProtocol.Flag)
            return true;
        return AgentProtocol.Str(handler, "command")?.Contains(AgentProtocol.Flag, StringComparison.Ordinal) == true;
    }

    /// <summary>Removes Glint's handlers everywhere, then groups and events left empty by that. Returns how many.</summary>
    static int RemoveGlintHandlers(JsonObject hooks)
    {
        var removed = 0;
        foreach (var name in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[name] is not JsonArray groups) continue;
            var touched = false;
            for (var i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i] is not JsonObject group || group["hooks"] is not JsonArray handlers) continue;
                for (var j = handlers.Count - 1; j >= 0; j--)
                {
                    if (handlers[j] is JsonObject h && IsGlintHandler(h))
                    {
                        handlers.RemoveAt(j);
                        removed++;
                        touched = true;
                    }
                }
                if (handlers.Count == 0 && touched) groups.RemoveAt(i);
            }
            if (groups.Count == 0 && touched) hooks.Remove(name);
        }
        return removed;
    }

    // ───────────── TOML helpers ─────────────

    static (int Start, int Length)? FindBlock(string text, string begin, string end)
    {
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0) return null;
        var stop = text.IndexOf(end, start, StringComparison.Ordinal);
        if (stop < 0) return null;
        stop += end.Length;
        // Take the line break after the end marker too.
        if (stop < text.Length && text[stop] == '\r') stop++;
        if (stop < text.Length && text[stop] == '\n') stop++;
        return (start, stop - start);
    }

    static string RemoveBlock(string text, string begin, string end)
    {
        while (FindBlock(text, begin, end) is { } block)
        {
            var before = text[..block.Start];
            var after = text[(block.Start + block.Length)..];
            // The blank line Glint put before its block goes too.
            if (before.EndsWith("\n\n", StringComparison.Ordinal)) before = before[..^1];
            else if (before.EndsWith("\r\n\r\n", StringComparison.Ordinal)) before = before[..^2];
            // And the one after the notify block at the top of the file.
            if (before.Length == 0) after = after.TrimStart('\r', '\n');
            text = before + after;
        }
        return text;
    }

    /// <summary>Literal string when possible (no escapes to get wrong), else a basic string.</summary>
    static string TomlString(string value) =>
        value.Contains('\'') || value.Contains('\n')
            ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\""
            : "'" + value + "'";

    /// <summary>
    /// The exe path written so that cmd, PowerShell and Git Bash all run it: forward slashes (bash eats backslashes)
    /// and, when the path has spaces, its 8.3 short form (each shell quotes differently). Quoted only as a last resort.
    /// </summary>
    static string ShellPath(string exe)
    {
        var path = exe;
        if (path.Contains(' '))
        {
            var buffer = new StringBuilder(520);
            var n = GetShortPathName(path, buffer, buffer.Capacity);
            if (n > 0 && n < buffer.Capacity) path = buffer.ToString();
        }
        path = path.Replace('\\', '/');
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);
}
