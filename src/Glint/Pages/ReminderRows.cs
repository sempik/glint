using System.Windows;
using System.Windows.Controls;
using Glint.Core;
using Glint.Services;

namespace Glint.Pages;

/// <summary>
/// One row of the upcoming-reminder lists on Главная and in Заметки → Напоминания: glyph, text, when, cancel.
/// The "when" label is returned so the page can refresh countdowns in place without rebuilding the row.
/// </summary>
internal static class ReminderRows
{
    public static Border Build(FrameworkElement owner, Reminder r, bool compact, out TextBlock when)
    {
        var glyph = new TextBlock
        {
            Style = (Style)owner.FindResource("Icon"),
            Text = Glyphs.Clock,
            FontSize = compact ? 12 : 14,
            Margin = new Thickness(0, 0, compact ? 8 : 10, 0),
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        var title = new TextBlock
        {
            Text = r.Title,
            FontSize = compact ? 12 : 13,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = r.Text,
        };
        when = new TextBlock
        {
            Style = (Style)owner.FindResource("MutedText"),
            Text = ReminderService.DescribeShort(r, DateTime.Now),
            FontSize = compact ? 11 : 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = r.Due.ToString("dd.MM.yyyy HH:mm"),
        };

        var cancel = new Button
        {
            Style = (Style)owner.FindResource("GhostButton"),
            Padding = new Thickness(compact ? 5 : 7, compact ? 3 : 5, compact ? 5 : 7, compact ? 3 : 5),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = r.IsTimer ? "Отменить таймер" : "Отменить напоминание",
            Focusable = false,
            Content = new TextBlock { Style = (Style)owner.FindResource("Icon"), Text = Glyphs.Close, FontSize = compact ? 8 : 9 },
        };
        ((TextBlock)cancel.Content).SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var id = r.Id;
        cancel.Click += (_, _) =>
        {
            try
            {
                ReminderService.Shared.Cancel(id);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        Grid.SetColumn(when, 2);
        Grid.SetColumn(cancel, 3);
        grid.Children.Add(glyph);
        grid.Children.Add(title);
        grid.Children.Add(when);
        grid.Children.Add(cancel);

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(compact ? 8 : 10),
            Padding = compact ? new Thickness(8, 1, 2, 1) : new Thickness(10, 4, 4, 4),
            Margin = new Thickness(0, 0, 0, compact ? 2 : 4),
        };
        row.SetResourceReference(Border.BackgroundProperty, compact ? "CardHoverBrush" : "CardBrush");
        return row;
    }

    /// <summary>Countdowns move every second only within the last hour; otherwise half a minute is plenty.</summary>
    public static TimeSpan RefreshInterval(IEnumerable<Reminder> items) =>
        items.Any(r => r.Remaining < TimeSpan.FromHours(1)) ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);
}
