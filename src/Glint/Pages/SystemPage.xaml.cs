using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glint.Services;

namespace Glint.Pages;

public partial class SystemPage : UserControl
{
    const string Dash = "—";
    const double GB = 1024d * 1024 * 1024;

    // Russian decimal comma regardless of the OS region, like ClipboardPageSupport.
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    readonly List<(TextBlock Name, TextBlock Cpu, TextBlock Ram)> _procRows = new();
    readonly List<(TextBlock Label, MeterBar Bar, TextBlock Size)> _driveRows = new();
    IDisposable? _subscription;
    string? _driveSignature;

    public SystemPage()
    {
        InitializeComponent();
        BuildProcessRows();

        // The pill caches pages and hides the panel instead of unloading it, so watch visibility too:
        // sampling must stop as soon as the user can't see the numbers.
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => UpdateSubscription();
        IsVisibleChanged += (_, _) => UpdateSubscription();

        // The meters and graphs draw themselves; repaint them with the new colors. Subscribed only while loaded,
        // so the static event never keeps a discarded page alive.
        // The colors may have changed while the page was off screen (e.g. on the Настройки tab).
        Loaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
            InvalidateGraphs(this);
        };
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    void OnThemeChanged(object? sender, EventArgs e) => InvalidateGraphs(this);

    static void InvalidateGraphs(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is MeterBar or Sparkline or CoreStrip) ((UIElement)child).InvalidateVisual();
            else InvalidateGraphs(child);
        }
    }

    // ───────────── Lifetime ─────────────

    void UpdateSubscription()
    {
        var wanted = IsLoaded && IsVisible;
        if (wanted && _subscription is null)
        {
            try
            {
                _subscription = SystemMonitor.Shared.Subscribe(OnSample);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return;
            }
            // Show the last numbers right away unless they are stale; fresh ones follow within a second.
            if (SystemMonitor.Shared.Latest is { } last && DateTime.Now - last.Time < TimeSpan.FromSeconds(3)) Render(last);
        }
        else if (!wanted && _subscription is not null)
        {
            _subscription.Dispose();
            _subscription = null;
        }
    }

    void OnSample(SystemSnapshot snapshot) => Dispatcher.BeginInvoke(() =>
    {
        // A sample can arrive just after the panel was hidden.
        if (_subscription is not null) Render(snapshot);
    });

    // ───────────── Rendering ─────────────

    void Render(SystemSnapshot s)
    {
        RenderCpu(s);
        RenderGpu(s);
        RenderMemory(s);
        RenderDisks(s);
        NetDown.Text = FormatSpeed(s.Network.DownBytesPerSec);
        NetUp.Text = FormatSpeed(s.Network.UpBytesPerSec);
        RenderProcesses(s);
    }

    void RenderCpu(SystemSnapshot s)
    {
        var cpu = s.Cpu;
        var shortName = ShortCpuName(cpu.Name);
        if (CpuName.Text != shortName)
        {
            CpuName.Text = shortName;
            CpuName.ToolTip = cpu.Name;
        }
        CpuLoad.Text = Percent(cpu.Load);
        CpuClock.Text = cpu.ClockMhz is double mhz ? string.Create(Ru, $"{mhz / 1000:0.00} ГГц") : Dash;
        CpuThreads.Text = $"{cpu.LogicalCores} {Plural(cpu.LogicalCores, "поток", "потока", "потоков")}";
        CpuUptime.Text = "в работе " + FormatUptime(cpu.Uptime);
        CpuSpark.Values = s.CpuHistory;
        CpuCores.Values = cpu.CoreLoads;
        CpuCores.Visibility = cpu.CoreLoads.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RenderGpu(SystemSnapshot s)
    {
        if (s.Gpu is not { } gpu)
        {
            GpuName.Text = "Не найдена";
            GpuLoad.Text = Dash;
            GpuTemp.Text = string.Empty;
            GpuClock.Visibility = GpuFan.Visibility = Visibility.Collapsed;
            GpuMemoryRow.Visibility = GpuPowerRow.Visibility = GpuSpark.Visibility = Visibility.Collapsed;
            return;
        }

        if (GpuName.Text != gpu.Name)
        {
            GpuName.Text = gpu.Name;
            GpuName.ToolTip = gpu.Name;
        }
        GpuLoad.Text = Percent(gpu.Load);
        GpuSpark.Visibility = gpu.Load is null ? Visibility.Collapsed : Visibility.Visible;
        GpuSpark.Values = s.GpuHistory;

        if (gpu.TemperatureC is double t)
        {
            GpuTemp.Text = $"{t:0} °C";
            var key = t >= 85 ? "DangerBrush" : t >= 70 ? "WarningBrush" : "TextBrush";
            if (!Equals(GpuTemp.Tag, key))
            {
                GpuTemp.Tag = key;
                GpuTemp.SetResourceReference(TextBlock.ForegroundProperty, key);
            }
        }
        else
        {
            GpuTemp.Text = string.Empty;
        }

        SetOptional(GpuClock, gpu.ClockMhz is double mhz ? $"{mhz:0} МГц" : null);
        SetOptional(GpuFan, gpu.FanPercent is double fan ? $"вентилятор {fan:0}%" : null);

        if (gpu.MemoryUsed is ulong used)
        {
            GpuMemoryRow.Visibility = Visibility.Visible;
            if (gpu.MemoryTotal is ulong total && total > 0)
            {
                GpuMemoryText.Text = string.Create(Ru, $"{used / GB:0.0} / {total / GB:0.0} ГБ");
                GpuMemoryBar.Value = used * 100.0 / total;
                GpuMemoryBar.Visibility = Visibility.Visible;
            }
            else
            {
                GpuMemoryText.Text = string.Create(Ru, $"{used / GB:0.0} ГБ");
                GpuMemoryBar.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            GpuMemoryRow.Visibility = Visibility.Collapsed;
        }

        if (gpu.PowerW is double watts)
        {
            GpuPowerRow.Visibility = Visibility.Visible;
            GpuPowerText.Text = gpu.PowerLimitW is double limit ? $"{watts:0} / {limit:0} Вт" : $"{watts:0} Вт";
        }
        else
        {
            GpuPowerRow.Visibility = Visibility.Collapsed;
        }
    }

    void RenderMemory(SystemSnapshot s)
    {
        var m = s.Memory;
        if (m.Total == 0)
        {
            RamText.Text = RamLoad.Text = CommitText.Text = Dash;
            return;
        }
        RamText.Text = string.Create(Ru, $"{m.Used / GB:0.0} из {m.Total / GB:0.0} ГБ");
        RamLoad.Text = Percent(m.Percent);
        CommitText.Text = m.CommitLimit > 0 ? string.Create(Ru, $"{m.CommitUsed / GB:0.0} / {m.CommitLimit / GB:0.0} ГБ") : Dash;
        RamBar.Value = m.Percent;
        RamSpark.Values = s.MemoryHistory;
    }

    void RenderDisks(SystemSnapshot s)
    {
        var a = s.DiskActivity;
        DiskActive.Text = a.ActivePercent is double active ? $"активность {active:0}%" : string.Empty;
        DiskIo.Text = a.ReadBytesPerSec is null && a.WriteBytesPerSec is null
            ? string.Empty
            : $"Чтение {FormatSpeed(a.ReadBytesPerSec)} · запись {FormatSpeed(a.WriteBytesPerSec)}";

        // Rebuild rows only when the set of drives changes (USB SSD plugged in), not every second.
        var signature = string.Join("|", s.Drives.Select(d => d.Name));
        if (signature != _driveSignature)
        {
            _driveSignature = signature;
            BuildDriveRows(s.Drives.Count);
        }

        for (var i = 0; i < s.Drives.Count && i < _driveRows.Count; i++)
        {
            var d = s.Drives[i];
            var row = _driveRows[i];
            row.Label.Text = d.Name;
            row.Label.ToolTip = string.IsNullOrWhiteSpace(d.Label) ? d.Name : $"{d.Label} ({d.Name})";
            row.Bar.Value = d.Percent;
            row.Size.Text = $"{FormatSize(d.Used)} / {FormatSize(d.Total)}";
        }
    }

    void BuildDriveRows(int count)
    {
        DriveList.Children.Clear();
        _driveRows.Clear();
        if (count == 0)
        {
            DriveList.Children.Add(new TextBlock { Style = (Style)FindResource("MutedText"), Text = "Нет локальных дисков" });
            return;
        }
        for (var i = 0; i < count; i++)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 96 });

            var label = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var bar = new MeterBar { VerticalAlignment = VerticalAlignment.Center };
            var size = new TextBlock
            {
                Style = (Style)FindResource("MutedText"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(size, 2);
            grid.Children.Add(label);
            grid.Children.Add(bar);
            grid.Children.Add(size);
            DriveList.Children.Add(grid);
            _driveRows.Add((label, bar, size));
        }
    }

    void BuildProcessRows()
    {
        for (var i = 0; i < 5; i++)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            var name = new TextBlock { FontSize = 12 };
            var cpu = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
            var ram = new TextBlock { Style = (Style)FindResource("MutedText"), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(cpu, 1);
            Grid.SetColumn(ram, 2);
            grid.Children.Add(name);
            grid.Children.Add(cpu);
            grid.Children.Add(ram);
            ProcList.Children.Add(grid);
            _procRows.Add((name, cpu, ram));
        }
    }

    void RenderProcesses(SystemSnapshot s)
    {
        ProcTitle.Text = s.Cpu.ProcessCount > 0 ? $"Процессы · {s.Cpu.ProcessCount}" : "Процессы";
        for (var i = 0; i < _procRows.Count; i++)
        {
            var (name, cpu, ram) = _procRows[i];
            if (i < s.TopProcesses.Count)
            {
                var p = s.TopProcesses[i];
                name.Text = p.Count > 1 ? $"{p.Name} ({p.Count})" : p.Name;
                name.ToolTip = name.Text;
                cpu.Text = p.CpuPercent >= 9.95 ? string.Create(Ru, $"{p.CpuPercent:0}%") : string.Create(Ru, $"{p.CpuPercent:0.0}%");
                ram.Text = $"{p.WorkingSet / (1024d * 1024):0} МБ";
            }
            else
            {
                name.Text = cpu.Text = ram.Text = string.Empty;
                name.ToolTip = null;
            }
        }
    }

    // ───────────── Formatting ─────────────

    static void SetOptional(TextBlock block, string? text)
    {
        block.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null) block.Text = text;
    }

    static string Percent(double? value) => value is double v ? $"{v:0}%" : Dash;

    static string FormatSize(ulong bytes) =>
        bytes >= 1000 * GB ? string.Create(Ru, $"{bytes / (GB * 1024):0.0} ТБ") : $"{bytes / GB:0} ГБ";

    /// <summary>Auto-scaled transfer rate: Б/с, КБ/с, МБ/с, ГБ/с.</summary>
    static string FormatSpeed(double? bytesPerSec)
    {
        if (bytesPerSec is not double b || !double.IsFinite(b)) return Dash;
        if (b < 1024) return $"{b:0} Б/с";
        var kb = b / 1024;
        if (kb < 1024) return kb < 100 ? string.Create(Ru, $"{kb:0.0} КБ/с") : $"{kb:0} КБ/с";
        var mb = kb / 1024;
        if (mb < 1024) return mb < 100 ? string.Create(Ru, $"{mb:0.0} МБ/с") : $"{mb:0} МБ/с";
        return string.Create(Ru, $"{mb / 1024:0.0} ГБ/с");
    }

    static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} д {t.Hours} ч";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} ч {t.Minutes} мин";
        return $"{Math.Max(0, t.Minutes)} мин";
    }

    static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    /// <summary>"AMD Ryzen 7 9800X3D 8-Core Processor" → "AMD Ryzen 7 9800X3D"; the full name stays in the tooltip.</summary>
    static string ShortCpuName(string name)
    {
        var s = Regex.Replace(name, @"\((R|TM)\)|®|™", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+@.*$", "");
        s = Regex.Replace(s, @"\s+(\d+|Dual|Quad|Six|Eight|Twelve|Sixteen)-Core\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+with Radeon.*$|\s+(CPU|Processor)\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s.Length == 0 ? name : s;
    }
}
