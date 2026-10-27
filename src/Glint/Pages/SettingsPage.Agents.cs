using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Glint.Core;
using Glint.Services.Agents;

namespace Glint.Pages;

/// <summary>
/// Настройки → Агенты: connecting Claude Code and Codex (their config files get Glint's hooks, after the user saw
/// exactly what will be added), and what the pill does with agent events. Toggles name their
/// <see cref="AgentSettings"/> property in Tag, like the Индикаторы card.
/// </summary>
public partial class SettingsPage
{
    readonly List<(int Seconds, RadioButton Button)> _agentTimeoutButtons = new();
    HookPlan? _agentPlan;

    static AgentSettings Agents => AppServices.Settings.Current.Agents;

    IEnumerable<CheckBox> AgentToggles =>
        LogicalTreeHelper.GetChildren(AgentOptionsPanel).OfType<CheckBox>().Where(c => c.Tag is string);

    void BuildAgentControls()
    {
        foreach (var seconds in AgentSettings.AskTimeouts)
        {
            var text = new TextBlock { Text = seconds < 60 ? $"{seconds} с" : $"{seconds / 60} мин", FontSize = 12 };
            var button = new RadioButton { Style = (Style)FindResource("TabButton"), GroupName = "AgentTimeout", Content = text };
            // The template's ContentPresenter sets the foreground; the implicit TextBlock style would override it.
            text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
            var value = seconds;
            button.Checked += (_, _) =>
            {
                if (_loading) return;
                Agents.AskTimeoutSeconds = value;
                Save();
            };
            AgentTimeoutPanel.Children.Add(button);
            _agentTimeoutButtons.Add((seconds, button));
        }
        // The files can be edited by hand (or by the agents) while Glint runs; re-read when the page shows.
        Loaded += (_, _) => UpdateAgentHookStatus();
    }

    void LoadAgentSettings()
    {
        foreach (var box in AgentToggles)
        {
            var p = typeof(AgentSettings).GetProperty((string)box.Tag, BindingFlags.Public | BindingFlags.Instance);
            if (p?.PropertyType == typeof(bool)) box.IsChecked = (bool)p.GetValue(Agents)!;
        }
        foreach (var (seconds, button) in _agentTimeoutButtons) button.IsChecked = seconds == Agents.AskTimeoutSeconds;
        UpdateAgentDependents();
        UpdateAgentHookStatus();
    }

    void AgentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { Tag: string name } box) return;
        var p = typeof(AgentSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p?.PropertyType != typeof(bool)) return;
        p.SetValue(Agents, box.IsChecked == true);
        UpdateAgentDependents();
        Save();
    }

    void UpdateAgentDependents()
    {
        var on = Agents.Enabled;
        foreach (var box in AgentToggles)
        {
            box.IsEnabled = box.Tag switch
            {
                "Enabled" => true,
                "SkipWhenTerminalActive" => on && Agents.AskInPill,
                _ => on,
            };
            box.Opacity = box.IsEnabled ? 1 : 0.45;
        }
        AgentTimeoutRow.IsEnabled = on && Agents.AskInPill;
        AgentTimeoutRow.Opacity = AgentTimeoutRow.IsEnabled ? 1 : 0.45;
    }

    // ───────────── Connecting ─────────────

    void UpdateAgentHookStatus()
    {
        ShowHookStatus(AgentKind.Claude, ClaudeHookStatus, ClaudeConnectButton, ClaudeConnectText, ClaudeDisconnectButton, "Claude Code");
        ShowHookStatus(AgentKind.Codex, CodexHookStatus, CodexConnectButton, CodexConnectText, CodexDisconnectButton, "Codex");
    }

    static void ShowHookStatus(AgentKind kind, TextBlock status, Button connect, TextBlock connectText, Button disconnect, string name)
    {
        var (state, exe) = AgentInstaller.GetStatus(kind);
        var file = AgentInstaller.PathFor(kind);
        var installed = Directory.Exists(Path.GetDirectoryName(file));
        status.Text = state switch
        {
            HookStatus.Connected => $"Подключён: {file}",
            HookStatus.Outdated => exe is not null && !AgentInstaller.IsThisExe(exe)
                ? $"Хуки ведут к другому Glint.exe ({exe}) — подключите заново"
                : "Хуки Glint устарели — подключите заново",
            HookStatus.Unreadable => $"Не удалось прочитать {file}",
            _ when !installed => $"Не подключён; {name} на этом компьютере не найден (нет папки {Path.GetDirectoryName(file)})",
            _ => "Не подключён",
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, state is HookStatus.Outdated or HookStatus.Unreadable ? "WarningTextBrush" : "MutedBrush");
        connect.Visibility = state == HookStatus.Connected ? Visibility.Collapsed : Visibility.Visible;
        connectText.Text = state == HookStatus.Outdated ? "Подключить заново" : $"Подключить {name}";
        disconnect.Visibility = state is HookStatus.Connected or HookStatus.Outdated ? Visibility.Visible : Visibility.Collapsed;
    }

    void AgentHookButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        var kind = tag.StartsWith("codex", StringComparison.Ordinal) ? AgentKind.Codex : AgentKind.Claude;
        var connect = tag.EndsWith(":on", StringComparison.Ordinal);
        AgentResult.Visibility = Visibility.Collapsed;
        try
        {
            _agentPlan = AgentInstaller.Plan(kind, connect);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowAgentResult(ex.Message, error: true);
            return;
        }
        var name = kind == AgentKind.Claude ? "Claude Code" : "Codex";
        var plan = _agentPlan;
        AgentConfirmTitle.Text = connect ? $"Подключить {name}?" : $"Отключить {name}?";
        var lines = new List<string>
        {
            $"Файл: {plan.FilePath}",
            plan.OriginalText is null
                ? "Файла ещё нет — Glint создаст его."
                : $"Сначала копия: {Path.GetFileName(plan.FilePath)}.glint-backup-<дата> в той же папке.",
        };
        if (connect) lines.Add($"Агент будет запускать: {AgentInstaller.ExePath}");
        lines.AddRange(plan.Notes);
        AgentConfirmText.Text = string.Join(Environment.NewLine, lines);
        AgentConfirmSnippet.Text = plan.Snippet.TrimEnd();
        AgentConfirmSnippet.Visibility = string.IsNullOrWhiteSpace(plan.Snippet) ? Visibility.Collapsed : Visibility.Visible;
        AgentConfirmButtonText.Text = connect ? "Подключить" : "Отключить";
        AgentConfirmButtonText.SetResourceReference(TextBlock.ForegroundProperty, connect ? "AccentBrush" : "DangerBrush");
        AgentConfirm.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => AgentConfirm.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void AgentConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        _agentPlan = null;
        AgentConfirm.Visibility = Visibility.Collapsed;
    }

    void AgentConfirmApply_Click(object sender, RoutedEventArgs e)
    {
        if (_agentPlan is not { } plan) return;
        _agentPlan = null;
        AgentConfirm.Visibility = Visibility.Collapsed;
        var name = plan.Agent == AgentKind.Claude ? "Claude Code" : "Codex";
        try
        {
            var backup = AgentInstaller.Apply(plan);
            var copy = backup is null ? "" : $" Копия прежнего файла: {backup}";
            if (plan.Connect)
            {
                // Connecting is the user saying "yes, listen": the pipe server starts with this save.
                Agents.Enabled = true;
                Save();
                _loading = true;
                LoadAgentSettings();
                _loading = false;
                var next = plan.Agent == AgentKind.Codex
                    ? " Откройте в Codex /hooks и доверьте хукам Glint."
                    : " Начните новую сессию Claude Code.";
                ShowAgentResult($"{name} подключён.{next}{copy}", error: false);
            }
            else
            {
                if (AgentInstaller.GetStatus(AgentKind.Claude).Status == HookStatus.NotConnected
                    && AgentInstaller.GetStatus(AgentKind.Codex).Status == HookStatus.NotConnected)
                {
                    Agents.Enabled = false;
                    Save();
                    _loading = true;
                    LoadAgentSettings();
                    _loading = false;
                }
                ShowAgentResult($"{name} отключён.{copy}", error: false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowAgentResult(ex.Message, error: true);
        }
        UpdateAgentHookStatus();
    }

    void ShowAgentResult(string text, bool error)
    {
        AgentResult.Text = text;
        AgentResult.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "MutedBrush");
        AgentResult.Visibility = Visibility.Visible;
    }

    /// <summary>Scrolls to the Агенты card (the Агенты tab's «Настройки агентов», the pill chip with agents off).</summary>
    public void RevealAgents()
    {
        Dispatcher.BeginInvoke(() => AgentsCard.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
