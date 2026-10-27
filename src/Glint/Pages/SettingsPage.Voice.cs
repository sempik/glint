using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glint.Core;
using Glint.Services.Voice;

namespace Glint.Pages;

/// <summary>
/// Настройки → Голосовой ввод: the optional dictation hotkey, the language, the engine (shown only when an
/// extra one is registered) and what Windows still needs before dictation can work.
/// </summary>
public partial class SettingsPage
{
    const string VoiceHotkeyIdle = "Кликните по полю и нажмите сочетание";

    readonly List<(string Id, RadioButton Button)> _voiceLanguageButtons = new();
    readonly List<(string Id, RadioButton Button)> _voiceEngineButtons = new();
    bool _capturingVoice;

    void BuildVoiceControls()
    {
        AddVoiceChoice(VoiceLanguagePanel, "VoiceLanguage", "auto", "Как в Windows", "Язык речи из параметров Windows", _voiceLanguageButtons, id => S.Voice.Language = id);
        // Installed speech languages, read once per page; an empty list is what the status line explains.
        foreach (var (tag, name) in WindowsSpeechEngine.SupportedLanguages())
            AddVoiceChoice(VoiceLanguagePanel, "VoiceLanguage", tag, Capitalize(name), tag, _voiceLanguageButtons, id => S.Voice.Language = id);

        var engines = DictationService.Shared.Engines;
        if (engines.Count > 1)
        {
            VoiceEngineRow.Visibility = Visibility.Visible;
            foreach (var (id, name) in engines)
                AddVoiceChoice(VoiceEnginePanel, "VoiceEngine", id, name, null, _voiceEngineButtons, value => S.Voice.Engine = value);
        }
        // A speech pack installed or the privacy switch flipped while Glint runs shows up next time the page opens.
        Loaded += (_, _) => UpdateVoiceStatus();
    }

    void AddVoiceChoice(Panel host, string group, string id, string label, string? tip, List<(string, RadioButton)> list, Action<string> apply)
    {
        var text = new TextBlock { Text = label, FontSize = 12 };
        var button = new RadioButton { Style = (Style)FindResource("TabButton"), GroupName = group, Content = text, ToolTip = tip };
        // The template's ContentPresenter sets the foreground; the implicit TextBlock style would override it.
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
        button.Checked += (_, _) =>
        {
            if (_loading) return;
            apply(id);
            Save();
        };
        host.Children.Add(button);
        list.Add((id, button));
    }

    void LoadVoiceSettings()
    {
        var v = S.Voice;
        VoiceHotkeyBox.Text = HotkeyText(v.Hotkey);
        Check(_voiceLanguageButtons, v.Language);
        Check(_voiceEngineButtons, v.Engine);
        UpdateVoiceStatus();

        static void Check(List<(string Id, RadioButton Button)> list, string value)
        {
            var match = list.FirstOrDefault(x => string.Equals(x.Id, value, StringComparison.OrdinalIgnoreCase)).Button;
            // A language uninstalled since: dictation falls back to the Windows one, so show that.
            (match ?? list.FirstOrDefault().Button)?.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        }
    }

    /// <summary>One line on what Windows has and lacks for dictation.</summary>
    void UpdateVoiceStatus()
    {
        var languages = WindowsSpeechEngine.SupportedLanguages();
        var online = WindowsSpeechEngine.OnlineRecognitionAllowed();
        var parts = new List<string>(2)
        {
            languages.Count == 0
                ? "в Windows не установлен ни один язык распознавания речи"
                : $"языки речи: {string.Join(", ", languages.Select(l => Capitalize(l.Name)))}",
        };
        if (online == false) parts.Add("«Распознавание речи в сети» выключено");
        var ready = languages.Count > 0 && online != false;
        VoiceStatus.Text = ready
            ? $"Готово: {parts[0]}"
            : $"Пока не готово: {string.Join("; ", parts)}. До тех пор кнопка микрофона откроет голосовой ввод Windows (Win+H)";
        VoiceStatus.SetResourceReference(TextBlock.ForegroundProperty, ready ? "MutedBrush" : "WarningTextBrush");
    }

    static string HotkeyText(string gesture) => string.IsNullOrEmpty(gesture) ? "Не задана" : gesture;

    static string Capitalize(string s) => s.Length > 0 ? char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..] : s;

    void VoiceHotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _capturingVoice = true;
        VoiceHotkeyBox.Text = "Нажмите сочетание…";
        VoiceHotkeyHint.Text = "Esc — отмена";
        VoiceHotkeyHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
    }

    void VoiceHotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _capturingVoice = false;
        VoiceHotkeyBox.Text = HotkeyText(S.Voice.Hotkey);
        if (VoiceHotkeyHint.Text == "Esc — отмена") VoiceHotkeyHint.Text = VoiceHotkeyIdle;
    }

    void VoiceHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingVoice) return;
        switch (ReadGesture(e, out var gesture, out var error))
        {
            case GestureRead.Cancel:
                EndVoiceCapture();
                return;
            case GestureRead.Error:
                ShowVoiceHotkeyError(error!);
                return;
            case GestureRead.Done:
                if (string.Equals(gesture, S.CommandBarHotkey, StringComparison.OrdinalIgnoreCase))
                {
                    ShowVoiceHotkeyError("Это сочетание уже у командной строки");
                    return;
                }
                S.Voice.Hotkey = gesture;
                Save();
                VoiceHotkeyHint.Text = "Нажмите его в любом окне — текст вставится туда";
                EndVoiceCapture();
                return;
        }
    }

    void ShowVoiceHotkeyError(string message)
    {
        VoiceHotkeyHint.Text = message;
        VoiceHotkeyHint.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
    }

    void EndVoiceCapture()
    {
        _capturingVoice = false;
        VoiceHotkeyBox.Text = HotkeyText(S.Voice.Hotkey);
        Keyboard.ClearFocus();
    }

    void VoiceHotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        VoiceHotkeyHint.Text = VoiceHotkeyIdle;
        VoiceHotkeyHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        if (S.Voice.Hotkey.Length == 0) return;
        S.Voice.Hotkey = string.Empty;
        VoiceHotkeyBox.Text = HotkeyText(string.Empty);
        Save();
    }

    void VoiceSettingsLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string page) DictationService.OpenSettings(page);
    }
}
