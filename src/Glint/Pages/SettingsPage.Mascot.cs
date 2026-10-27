using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glint.Core;
using Glint.Mascots;

namespace Glint.Pages;

/// <summary>
/// Настройки → Персонаж: which character sits in the pill (or none), its size and side, and what it reacts to.
/// The picker shows each character drawn still, in its rest pose. Toggles name their <see cref="MascotSettings"/>
/// property in Tag, like the Индикаторы and Агенты cards.
/// </summary>
public partial class SettingsPage
{
    const double MascotPreviewSize = 30;

    readonly List<(string Id, Border Ring, MascotView? Preview)> _mascotChoices = new();
    readonly List<(int Size, RadioButton Button)> _mascotSizeButtons = new();
    readonly List<(string Side, RadioButton Button)> _mascotSideButtons = new();
    readonly List<(int Minutes, RadioButton Button)> _mascotSleepButtons = new();

    static MascotSettings Mascot => AppServices.Settings.Current.Mascot;

    IEnumerable<CheckBox> MascotToggles =>
        LogicalTreeHelper.GetChildren(MascotOptionsPanel).OfType<CheckBox>().Where(c => c.Tag is string);

    void BuildMascotControls()
    {
        AddMascotChoice(MascotSettings.None, "Нет", "Без персонажа", null);
        foreach (var design in MascotDesigns.All) AddMascotChoice(design.Id, design.Name, design.Blurb, design);

        var sizeNames = new[] { "Маленький", "Средний", "Крупный" };
        for (var i = 0; i < MascotSettings.Sizes.Length; i++)
        {
            var size = MascotSettings.Sizes[i];
            _mascotSizeButtons.Add((size, AddSegment(MascotSizePanel, "MascotSize", sizeNames[Math.Min(i, sizeNames.Length - 1)], () =>
            {
                if (Mascot.Size == size) return;
                Mascot.Size = size;
                Save();
            })));
        }
        foreach (var (side, name) in new[] { (MascotSettings.Left, "Слева"), (MascotSettings.Right, "Справа") })
        {
            _mascotSideButtons.Add((side, AddSegment(MascotSidePanel, "MascotSide", name, () =>
            {
                if (Mascot.Side == side) return;
                Mascot.Side = side;
                Save();
            })));
        }
        foreach (var minutes in MascotSettings.SleepChoices)
        {
            _mascotSleepButtons.Add((minutes, AddSegment(MascotSleepPanel, "MascotSleep", $"{minutes} мин", () =>
            {
                if (Mascot.SleepMinutes == minutes) return;
                Mascot.SleepMinutes = minutes;
                Save();
            })));
        }
    }

    /// <summary>A segmented-control button like the other rows of Настройки.</summary>
    RadioButton AddSegment(Panel panel, string group, string label, Action onChecked)
    {
        var text = new TextBlock { Text = label, FontSize = 12 };
        var button = new RadioButton { Style = (Style)FindResource("TabButton"), GroupName = group, Content = text };
        // The template's ContentPresenter sets the foreground; the implicit TextBlock style would override it.
        text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
        button.Checked += (_, _) =>
        {
            if (!_loading) onChecked();
        };
        panel.Children.Add(button);
        return button;
    }

    void AddMascotChoice(string id, string name, string tip, MascotDesign? design)
    {
        FrameworkElement face;
        MascotView? preview = null;
        if (design is not null)
        {
            preview = new MascotView { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            preview.Configure(design, MascotPreviewSize, MascotPreviewSize + 4, lane: false, mirror: false, Mascot.ThemeColors);
            face = preview;
        }
        else
        {
            var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = Glyphs.Close, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            face = icon;
        }
        var ring = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Child = face,
        };
        ring.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        var label = new TextBlock { Text = name, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var button = new Button
        {
            Style = (Style)FindResource("SwatchButton"),
            Width = 64,
            Padding = new Thickness(0, 2, 0, 2),
            ToolTip = tip,
            Content = new StackPanel { Children = { ring, label } },
        };
        button.Click += (_, _) =>
        {
            if (Mascot.Character == id) return;
            Mascot.Character = id;
            UpdateMascotChoices();
            UpdateMascotDependents();
            Save();
        };
        MascotPicker.Children.Add(button);
        _mascotChoices.Add((id, ring, preview));
    }

    void LoadMascotSettings()
    {
        var m = Mascot;
        foreach (var box in MascotToggles)
        {
            var p = typeof(MascotSettings).GetProperty((string)box.Tag, BindingFlags.Public | BindingFlags.Instance);
            if (p?.PropertyType == typeof(bool)) box.IsChecked = (bool)p.GetValue(m)!;
        }
        foreach (var (size, button) in _mascotSizeButtons) button.IsChecked = size == m.Size;
        foreach (var (side, button) in _mascotSideButtons) button.IsChecked = side == m.Side;
        foreach (var (minutes, button) in _mascotSleepButtons) button.IsChecked = minutes == m.SleepMinutes;
        UpdateMascotChoices();
        UpdateMascotDependents();
    }

    void MascotToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { Tag: string name } box) return;
        var p = typeof(MascotSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p?.PropertyType != typeof(bool)) return;
        p.SetValue(Mascot, box.IsChecked == true);
        // Previews follow the colour choice.
        if (name == nameof(MascotSettings.ThemeColors)) UpdateMascotChoices();
        UpdateMascotDependents();
        Save();
    }

    void UpdateMascotChoices()
    {
        foreach (var (id, ring, preview) in _mascotChoices)
        {
            if (id == Mascot.Character) ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            else ring.BorderBrush = Brushes.Transparent;
            if (preview?.Design is { } design)
                preview.Configure(design, MascotPreviewSize, MascotPreviewSize + 4, lane: false, mirror: false, Mascot.ThemeColors);
        }
    }

    void UpdateMascotDependents()
    {
        var on = Mascot.Enabled;
        MascotOptionsPanel.IsEnabled = on;
        MascotOptionsPanel.Opacity = on ? 1 : 0.45;
        MascotSleepRow.IsEnabled = on && Mascot.Sleep;
        MascotSleepRow.Opacity = MascotSleepRow.IsEnabled ? 1 : 0.45;
    }
}
