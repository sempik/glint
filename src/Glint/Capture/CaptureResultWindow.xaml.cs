using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Glint.Services;

namespace Glint.Capture;

/// <summary>One button under the result: a label, a glyph and what it does with the (possibly edited) text.</summary>
public sealed record ResultAction(string Label, string Glyph, Action<string> Run, bool Primary = false);

/// <summary>
/// Shows what QR or text recognition found, with actions (copy, open link, copy Wi-Fi password). The text can
/// be corrected before copying. Opens in the middle of the monitor the cursor is on; Esc closes it.
/// </summary>
public partial class CaptureResultWindow : Window
{
    CaptureResultWindow(string title, string subtitle, string glyph, string text, IReadOnlyList<ResultAction> actions)
    {
        InitializeComponent();
        Background = new SolidColorBrush(ThemeService.Tint);
        HeaderTitle.Text = title;
        HeaderSubtitle.Text = subtitle;
        HeaderSubtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        HeaderIcon.Text = glyph;
        ResultBox.Text = text;

        foreach (var action in actions)
        {
            var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = action.Glyph, FontSize = 13 };
            var label = new TextBlock { Text = action.Label, Margin = new Thickness(8, 0, 0, 0), FontSize = 12.5 };
            if (action.Primary)
            {
                icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            }
            var button = new Button
            {
                Style = (Style)FindResource(action.Primary ? "TileButton" : "GhostButton"),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(6, 0, 0, 0),
                Focusable = false,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
            };
            button.Click += (_, _) =>
            {
                try
                {
                    action.Run(ResultBox.Text);
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
                Close();
            };
            Actions.Children.Add(button);
        }

        CloseButton.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        // Grab the whole card to move it out of the way.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not TextBox && e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    public static void Open(string title, string subtitle, string glyph, string text, IReadOnlyList<ResultAction> actions)
    {
        var window = new CaptureResultWindow(title, subtitle, glyph, text, actions);
        window.Show();
        window.Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Windows 11 rounds the corners of a borderless window when asked; Windows 10 ignores it.
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
