using System.Windows;
using System.Windows.Controls;
using Glint.Core;

namespace Glint.Pages;

/// <summary>Настройки → Экран: screenshot editor, hiding Glint from captures, recording sound, cursor and frame rate.</summary>
public partial class SettingsPage
{
    static readonly int[] FrameRates = { 30, 60 };
    readonly List<(int Fps, RadioButton Button)> _fpsButtons = new();

    void BuildCaptureControls()
    {
        foreach (var fps in FrameRates)
        {
            var text = new TextBlock { Text = fps.ToString(), FontSize = 12 };
            var button = new RadioButton { Style = (Style)FindResource("TabButton"), GroupName = "RecordFps", Content = text };
            // The template's ContentPresenter sets the foreground; the implicit TextBlock style would override it.
            text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
            var value = fps;
            button.Checked += (_, _) =>
            {
                if (_loading || S.Capture.RecordFrameRate == value) return;
                S.Capture.RecordFrameRate = value;
                Save();
            };
            FrameRatePanel.Children.Add(button);
            _fpsButtons.Add((fps, button));
        }
    }

    void LoadCaptureSettings()
    {
        var c = S.Capture;
        CaptureEditorToggle.IsChecked = c.OpenEditor;
        CaptureHideGlintToggle.IsChecked = c.HideGlint;
        RecordAudioToggle.IsChecked = c.RecordSystemAudio;
        RecordCursorToggle.IsChecked = c.RecordCursor;
        foreach (var (fps, button) in _fpsButtons) button.IsChecked = fps == c.RecordFrameRate;
    }

    void CaptureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var c = S.Capture;
        c.OpenEditor = CaptureEditorToggle.IsChecked == true;
        c.HideGlint = CaptureHideGlintToggle.IsChecked == true;
        c.RecordSystemAudio = RecordAudioToggle.IsChecked == true;
        c.RecordCursor = RecordCursorToggle.IsChecked == true;
        Save();
    }
}
