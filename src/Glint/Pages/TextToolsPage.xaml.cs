using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services;
using Glint.Services.Voice;

namespace Glint.Pages;

public partial class TextToolsPage : UserControl
{
    // Segoe Fluent "Paste"; not in Core.Glyphs.
    const string PasteGlyph = "\uE77F";

    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly TextBoxDictationTarget _dictation;

    public TextToolsPage()
    {
        InitializeComponent();

        _dictation = new TextBoxDictationTarget(Input, "Текст");
        MicIcon.Text = Glyphs.Microphone;
        // The pill caches pages; follow dictation only while attached.
        Loaded += (_, _) => { DictationService.Shared.Changed -= OnDictationChanged; DictationService.Shared.Changed += OnDictationChanged; UpdateMic(); };
        Unloaded += (_, _) => DictationService.Shared.Changed -= OnDictationChanged;

        PasteIcon.Text = PasteGlyph;
        CopyIcon.Text = Glyphs.Copy;
        ClearIcon.Text = Glyphs.Delete;

        AddTools(ToolsGrid, new (string, Func<string, string>)[]
        {
            ("Раскладка", TextTransforms.SwitchLayout),
            ("ВЕРХНИЙ", TextTransforms.Upper),
            ("нижний", TextTransforms.Lower),
            ("Каждое Слово", TextTransforms.TitleCase),
            ("Предложение", TextTransforms.SentenceCase),
            ("Транслит", TextTransforms.Transliterate),
            ("Убрать переносы", TextTransforms.RemoveLineBreaks),
            ("Лишние пробелы", TextTransforms.CollapseSpaces),
        });
        AddTools(EncodingGrid, new (string, Func<string, string>)[]
        {
            ("JSON красиво", TextTransforms.JsonPretty),
            ("JSON в строку", TextTransforms.JsonMinify),
            ("→ Base64", TextTransforms.ToBase64),
            ("Base64 →", TextTransforms.FromBase64),
            ("URL encode", TextTransforms.UrlEncode),
            ("URL decode", TextTransforms.UrlDecode),
        });

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); HideStatus(); };
        UpdateStats();
    }

    void AddTools(Panel host, (string Label, Func<string, string> Transform)[] tools)
    {
        foreach (var (label, transform) in tools)
        {
            var button = new Button
            {
                Style = (Style)FindResource("TileButton"),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Height = 38,
                Margin = new Thickness(3),
                Content = new TextBlock { Text = label, FontSize = 12.5, TextAlignment = TextAlignment.Center },
                ToolTip = label,
                // Keep focus (and the visible selection) in the editor.
                Focusable = false,
            };
            button.Click += (_, _) => ApplyTool(label, transform);
            host.Children.Add(button);
        }
    }

    void ModeTab_Checked(object sender, RoutedEventArgs e)
    {
        // Fires once during InitializeComponent, before the grids exist.
        if (ToolsGrid is null || EncodingGrid is null) return;
        ToolsGrid.Visibility = sender == ToolsTab ? Visibility.Visible : Visibility.Collapsed;
        EncodingGrid.Visibility = sender == EncodingTab ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────── Actions ─────────────

    void ApplyTool(string name, Func<string, string> transform)
    {
        var hasSelection = Input.SelectionLength > 0;
        var source = hasSelection ? Input.SelectedText : Input.Text;
        if (string.IsNullOrEmpty(source))
        {
            ShowStatus("Сначала вставьте или наберите текст", success: false);
            return;
        }

        string result;
        try
        {
            result = transform(source);
        }
        catch (FormatException ex)
        {
            ShowStatus(ex.Message, success: false);
            return;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowStatus("Не получилось преобразовать текст", success: false);
            return;
        }

        // Replacing through SelectedText keeps the change on the TextBox undo stack (Ctrl+Z works).
        var start = hasSelection ? Input.SelectionStart : 0;
        if (!hasSelection) Input.Select(0, Input.Text.Length);
        Input.SelectedText = result;
        if (hasSelection) Input.Select(start, result.Length);
        else Input.Select(result.Length, 0);

        var copied = TryCopy(result);
        ShowStatus(copied ? $"{name} — готово, уже в буфере" : $"{name} — готово", success: true);
    }

    void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                ShowStatus("В буфере нет текста", success: false);
                return;
            }
            text = System.Windows.Clipboard.GetText();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowStatus("Буфер обмена занят, попробуйте ещё раз", success: false);
            return;
        }

        var caret = Input.SelectionStart;
        Input.SelectedText = text;
        Input.Select(caret + text.Length, 0);
        ShowStatus("Вставлено из буфера", success: true);
    }

    void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = Input.SelectionLength > 0 ? Input.SelectedText : Input.Text;
        if (string.IsNullOrEmpty(text))
        {
            ShowStatus("Нечего копировать", success: false);
            return;
        }
        if (TryCopy(text)) ShowStatus("Скопировано", success: true);
        else ShowStatus("Буфер обмена занят, попробуйте ещё раз", success: false);
    }

    void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Input.Clear();
        HideStatus();
    }

    // ───────────── Dictation ─────────────

    void MicButton_Click(object sender, RoutedEventArgs e)
    {
        // The caret marks where the words go, and a focused editor keeps the panel open while it listens.
        if (!Input.IsKeyboardFocused)
        {
            Input.Focus();
            Input.Select(Input.Text.Length, 0);
        }
        DictationService.Shared.Toggle(_dictation);
    }

    // Raised on the UI thread.
    void OnDictationChanged(object? sender, EventArgs e) => UpdateMic();

    void UpdateMic()
    {
        var d = DictationService.Shared;
        var mine = ReferenceEquals(d.Target, _dictation);
        MicIcon.SetResourceReference(TextBlock.ForegroundProperty, mine ? "DangerBrush" : "TextBrush");
        MicButton.ToolTip = mine ? "Закончить диктовку" : "Надиктовать текст";
        // The phrase being heard shows in place of the counters until it lands in the editor.
        if (mine && d.Partial is { } partial) StatsText.Text = "Слышу: " + partial;
        else if (mine) StatsText.Text = d.IsStarting ? "Включаю микрофон…" : "Говорите — пауза или кнопка закончат";
        else UpdateStats();
    }

    static bool TryCopy(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            // Another process may hold the clipboard open (CLIPBRD_E_CANT_OPEN).
            App.LogError(ex);
            return false;
        }
    }

    // ───────────── Stats & status ─────────────

    void Input_TextChanged(object sender, TextChangedEventArgs e) => UpdateStats();

    void UpdateStats()
    {
        // Called from InitializeComponent's first TextChanged too, before later fields are wired.
        if (StatsText is null || Placeholder is null) return;
        var text = Input.Text;
        var words = TextTransforms.CountWords(text);
        Placeholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
        StatsText.Text =
            $"{TextTransforms.CountChars(text)} симв. · {TextTransforms.CountCharsNoSpaces(text)} без пробелов · " +
            $"{words} {WordsLabel(words)} · {TextTransforms.CountLines(text)} стр.";
    }

    static string WordsLabel(int n)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return "слов";
        return mod10 switch { 1 => "слово", >= 2 and <= 4 => "слова", _ => "слов" };
    }

    void ShowStatus(string message, bool success)
    {
        var color = (Color)FindResource(success ? "SuccessColor" : "DangerColor");
        var brush = (Brush)FindResource(success ? "SuccessBrush" : "DangerBrush");
        StatusChip.Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
        StatusIcon.Text = success ? Glyphs.Check : Glyphs.Info;
        StatusIcon.Foreground = brush;
        StatusText.Text = message;
        StatusText.Foreground = brush;
        StatusText.ToolTip = message;

        StatusChip.Visibility = Visibility.Visible;
        StatusChip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    void HideStatus()
    {
        _statusTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            if (StatusChip.Opacity == 0) StatusChip.Visibility = Visibility.Collapsed;
        };
        StatusChip.BeginAnimation(OpacityProperty, fade);
    }
}
