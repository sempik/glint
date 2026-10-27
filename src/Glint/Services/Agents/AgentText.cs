using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glint.Services.Agents;

/// <summary>Turns an agent's tool call into short Russian text for the request card: what it wants and a preview.</summary>
public static class AgentText
{
    const int PreviewChars = 600;
    /// <summary>A permission card shows its command whole, up to this; anything longer can't be approved in Glint.</summary>
    const int RequestPreviewChars = 4000;

    // Wrappers whose "-c script" form says nothing beyond the script itself.
    static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "bash", "sh", "zsh", "cmd", "powershell", "pwsh" };

    /// <summary>"Выполнить команду", "Изменить файл"… for a tool name from Claude Code or Codex.</summary>
    public static string Action(string tool) => tool switch
    {
        "Bash" or "shell" or "exec_command" or "local_shell" => "Выполнить команду",
        "PowerShell" => "Выполнить команду PowerShell",
        "Write" => "Создать или перезаписать файл",
        "Edit" or "MultiEdit" or "apply_patch" => "Изменить файл",
        "NotebookEdit" => "Изменить блокнот",
        "Read" => "Прочитать файл",
        "WebFetch" => "Открыть адрес",
        "WebSearch" => "Поискать в интернете",
        "ExitPlanMode" => "Утвердить план и начать",
        "Task" or "Agent" => "Запустить помощника",
        "AskUserQuestion" => "Спрашивает",
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "Инструмент MCP",
        _ => $"Инструмент {tool}",
    };

    /// <summary>The part of the input worth reading before saying yes: the command, the file and its new text, the URL.</summary>
    public static string Preview(string tool, JsonObject? input, string? cwd)
    {
        var text = FullPreview(tool, input, cwd);
        return text.Length <= PreviewChars ? text : text[..PreviewChars] + "…";
    }

    /// <summary>
    /// The preview for a permission card: whole, since «Разрешить» approves exactly this. Only a monster past
    /// <see cref="RequestPreviewChars"/> is cut, and <paramref name="cut"/> then tells the card not to offer approval.
    /// </summary>
    public static string RequestPreview(string tool, JsonObject? input, string? cwd, out bool cut)
    {
        var text = FullPreview(tool, input, cwd);
        cut = text.Length > RequestPreviewChars;
        return cut ? text[..RequestPreviewChars] + "…" : text;
    }

    static string FullPreview(string tool, JsonObject? input, string? cwd)
    {
        if (input is null) return tool;
        var edits = (input["edits"] as JsonArray)?.Count ?? 0;
        string? text = tool switch
        {
            "Bash" or "PowerShell" or "shell" or "exec_command" or "local_shell" => Command(input["command"] ?? input["cmd"]),
            "Write" => Join(RelativePath(Str(input, "file_path"), cwd), FirstLines(Str(input, "content"), 4)),
            "Edit" => Join(RelativePath(Str(input, "file_path"), cwd), FirstLines(Str(input, "new_string"), 4)),
            "MultiEdit" => Join(RelativePath(Str(input, "file_path"), cwd), $"{edits} {Plural(edits, "правка", "правки", "правок")}"),
            "NotebookEdit" => RelativePath(Str(input, "notebook_path"), cwd),
            "Read" => RelativePath(Str(input, "file_path"), cwd),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "ExitPlanMode" => FirstLines(Str(input, "plan"), 8),
            "Task" or "Agent" => Join(Str(input, "description"), FirstLines(Str(input, "prompt"), 3)),
            "apply_patch" => FirstLines(Str(input, "patch") ?? Str(input, "input") ?? Command(input["command"]), 8),
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => Join(McpName(tool), Json(input)),
            _ => null,
        };
        return (text ?? Json(input)).Trim();
    }

    /// <summary>Russian plural: 1 правка, 2 правки, 5 правок, 21 правка, 11 правок.</summary>
    internal static string Plural(int n, string one, string few, string many)
    {
        n = Math.Abs(n);
        if (n % 100 is >= 11 and <= 14) return many;
        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>"server · tool" from "mcp__server__tool".</summary>
    static string McpName(string tool)
    {
        var parts = tool.Split("__", StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? $"{parts[1]} · {string.Join("__", parts.Skip(2))}" : tool;
    }

    /// <summary>A command as a string, or as Codex's argv array ("bash", "-lc", "…").</summary>
    static string? Command(JsonNode? node)
    {
        if (node is JsonValue) return Str(node);
        if (node is not JsonArray parts) return null;
        var argv = parts.Select(p => Str(p) ?? "").ToList();
        // A bare shell wrapper ("bash -lc script") says nothing; the script it runs is the interesting part. Anything
        // else is shown whole, so no argument hides behind a "-c" that isn't a shell's.
        if (argv.Count == 3 && argv[1] is ("-lc" or "-c" or "/c" or "/C" or "-Command" or "-command") &&Shells.Contains(ShellName(argv[0])))
            return argv[2];
        return string.Join(" ", argv.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    /// <summary>"bash" from "/bin/bash", "cmd" from "C:\Windows\System32\cmd.exe".</summary>
    static string ShellName(string program)
    {
        var name = program.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static string? RelativePath(string? path, string? cwd)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (!string.IsNullOrEmpty(cwd) && Path.IsPathFullyQualified(path))
            {
                var rel = Path.GetRelativePath(cwd, path);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathFullyQualified(rel)) return rel;
            }
        }
        catch (Exception)
        {
            // An odd path is shown as it came.
        }
        return path;
    }

    public static string? FirstLines(string? text, int lines)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var all = text.Replace("\r\n", "\n").Split('\n');
        var head = string.Join("\n", all.Take(lines));
        return all.Length > lines ? head + "\n…" : head;
    }

    /// <summary>The first non-empty line, shortened: for the session list and flashes.</summary>
    public static string? OneLine(string? text, int max = 110)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('#', '*', '-', '>', ' ')).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        line = line.Replace("**", "").Replace("`", "");
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }

    static string? Join(string? a, string? b) =>
        string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + "\n" + b;

    static string Json(JsonNode node)
    {
        try
        {
            return node.ToJsonString(new JsonSerializerOptions(AgentProtocol.Compact) { WriteIndented = true });
        }
        catch (Exception)
        {
            return node.ToString();
        }
    }

    static string? Str(JsonNode? node, string name) => AgentProtocol.Str(node, name);

    static string? Str(JsonNode? node)
    {
        try
        {
            return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"Bash(npm test:*)", "режим «принимать правки»"… for Claude's "don't ask again" suggestions.</summary>
    public static string? DescribeSuggestions(JsonArray? suggestions)
    {
        if (suggestions is null || suggestions.Count == 0) return null;
        var parts = new List<string>();
        foreach (var s in suggestions.OfType<JsonObject>())
        {
            switch (Str(s, "type"))
            {
                case "addRules" or "replaceRules":
                    foreach (var rule in (s["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        var name = Str(rule, "toolName");
                        var content = Str(rule, "ruleContent");
                        if (name is not null) parts.Add(string.IsNullOrEmpty(content) ? name : $"{name}({content})");
                    }
                    break;
                case "setMode":
                    parts.Add(Str(s, "mode") switch
                    {
                        "acceptEdits" => "режим «принимать правки»",
                        "bypassPermissions" => "режим «без вопросов»",
                        "plan" => "режим плана",
                        var m => $"режим {m}",
                    });
                    break;
                case "addDirectories":
                    foreach (var d in (s["directories"] as JsonArray ?? new JsonArray())) parts.Add("папка " + Str(d));
                    break;
            }
        }
        return parts.Count == 0 ? "так, как предлагает Claude Code" : string.Join(", ", parts);
    }

    /// <summary>StopFailure error ids in plain Russian.</summary>
    public static string Error(string? error, string? details) => error switch
    {
        "rate_limit" => "упёрся в лимит запросов",
        "overloaded" => "сервис перегружен",
        "authentication_failed" => "нужно войти заново",
        "billing_error" => "проблема с оплатой",
        "server_error" => "ошибка на стороне сервиса",
        "max_output_tokens" => "ответ вышел слишком длинным",
        "model_not_found" => "модель не найдена",
        _ => OneLine(details, 80) ?? "ответ прервался ошибкой",
    };
}
