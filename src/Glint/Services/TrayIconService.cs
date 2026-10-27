using System.Runtime.InteropServices;
using Glint.Core;
using Glint.Pill;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Glint.Services;

/// <summary>Notification-area icon with a small menu; left click opens the panel.</summary>
public sealed class TrayIconService : IDisposable
{
    readonly Action _onExit;
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ContextMenuStrip _menu;
    readonly Forms.ToolStripMenuItem _autostartItem;
    Drawing.Icon? _drawnIcon;
    IntPtr _hIcon;
    bool _disposed;

    public TrayIconService(Action onExit)
    {
        _onExit = onExit;

        DarkColors.UpdateFromTheme();
        var drawn = CreateIcon();
        _drawnIcon = drawn.Icon;
        _hIcon = drawn.Handle;

        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false },
            ShowImageMargin = false,
            ShowCheckMargin = true,
            BackColor = DarkColors.Background,
            ForeColor = DarkColors.Text,
            Font = new Drawing.Font("Segoe UI", 9f),
        };

        _autostartItem = new Forms.ToolStripMenuItem("Запускать с Windows") { CheckOnClick = false };
        _autostartItem.Click += (_, _) => OnUi(ToggleAutostart);

        _menu.Items.Add(MakeItem("Открыть", () => ExpandPanel(PillWindow.HomeTab)));
        var commandBarItem = MakeItem("Командная строка (Alt+Space)", () => AppServices.ToggleCommandBar());
        _menu.Items.Add(commandBarItem);
        _menu.Items.Add(MakeItem("Настройки", () => ExpandPanel(PillWindow.SettingsTab)));
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(MakeItem("Выход", () => _onExit()));
        foreach (Forms.ToolStripItem item in _menu.Items) item.ForeColor = DarkColors.Text;

        // Registry can be changed from outside (Task Manager's Startup tab), so read it each time.
        _menu.Opening += (_, _) =>
        {
            _autostartItem.Checked = Autostart.IsEnabled;
            var hotkey = AppServices.Settings?.Current.CommandBarHotkey;
            commandBarItem.Text = string.IsNullOrEmpty(hotkey) ? "Командная строка" : $"Командная строка ({hotkey})";
        };

        _icon = new Forms.NotifyIcon
        {
            Text = "Glint",
            Icon = _drawnIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OnUi(() => ExpandPanel(PillWindow.HomeTab));
        };

        ThemeService.ThemeChanged += OnThemeChanged;
    }

    /// <summary>Redraws the icon and recolors the menu with the new accents.</summary>
    void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            DarkColors.UpdateFromTheme();
            _menu.BackColor = DarkColors.Background;
            _menu.Invalidate();

            var drawn = CreateIcon();
            if (drawn.Icon is null) return;
            var oldIcon = _drawnIcon;
            var oldHandle = _hIcon;
            _drawnIcon = drawn.Icon;
            _hIcon = drawn.Handle;
            // Switch first, then free the old HICON the shell no longer uses.
            _icon.Icon = _drawnIcon;
            oldIcon?.Dispose();
            if (oldHandle != IntPtr.Zero) Interop.DestroyIcon(oldHandle);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static Forms.ToolStripMenuItem MakeItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => OnUi(action);
        return item;
    }

    static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.InvokeAsync(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        });
    }

    static void ExpandPanel(int tab)
    {
        if (AppServices.Notifier is not PillWindow pill) return;
        // Activating lets the normal Deactivated → collapse path close the panel when the user clicks away.
        pill.Activate();
        pill.Expand(tab);
    }

    /// <summary>System notification, used when the pill itself can't be seen (e.g. over a fullscreen app).</summary>
    public void ShowBalloon(string title, string? text)
    {
        if (_disposed) return;
        try
        {
            _icon.ShowBalloonTip(8000, title, string.IsNullOrEmpty(text) ? "Glint" : text, Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static void ToggleAutostart()
    {
        Autostart.Set(!Autostart.IsEnabled);
        AppServices.Settings.Save();
    }

    static (Drawing.Icon? Icon, IntPtr Handle) CreateIcon()
    {
        try
        {
            using var bmp = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);

                // Dark pill with a thin gradient rim.
                var pill = new Drawing.RectangleF(1.5f, 8.5f, 29f, 15f);
                using var path = RoundedRect(pill, 7.5f);
                var a1 = ThemeService.Accent;
                var a2 = ThemeService.Accent2;
                var tint = ThemeService.Tint;
                using (var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, Math.Min(255, tint.R + 4), Math.Min(255, tint.G + 4), Math.Min(255, tint.B + 6))))
                    g.FillPath(fill, path);
                using (var rimBrush = new Drawing2D.LinearGradientBrush(pill, Drawing.Color.FromArgb(220, a1.R, a1.G, a1.B), Drawing.Color.FromArgb(220, a2.R, a2.G, a2.B), 0f))
                using (var rim = new Drawing.Pen(rimBrush, 1.5f))
                    g.DrawPath(rim, path);

                // The glint: soft halo plus a gradient dot on the right side of the pill.
                var dot = new Drawing.RectangleF(18f, 11.5f, 9f, 9f);
                // Halo is the midpoint of both accents (the original violet/teal pair gave a soft blue).
                using (var halo = new Drawing.SolidBrush(Drawing.Color.FromArgb(70, (a1.R + a2.R) / 2, (a1.G + a2.G) / 2, (a1.B + a2.B) / 2)))
                    g.FillEllipse(halo, Drawing.RectangleF.Inflate(dot, 2f, 2f));
                using (var dotBrush = new Drawing2D.LinearGradientBrush(dot, Drawing.Color.FromArgb(255, a1.R, a1.G, a1.B), Drawing.Color.FromArgb(255, a2.R, a2.G, a2.B), 45f))
                    g.FillEllipse(dotBrush, dot);

                // Short bar on the left hints at the clock text.
                using var bar = new Drawing.SolidBrush(Drawing.Color.FromArgb(230, 245, 245, 247));
                using var barPath = RoundedRect(new Drawing.RectangleF(6f, 14.5f, 9f, 3f), 1.5f);
                g.FillPath(bar, barPath);
            }

            var handle = bmp.GetHicon();
            return (Drawing.Icon.FromHandle(handle), handle);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return (null, IntPtr.Zero);
        }
    }

    static Drawing2D.GraphicsPath RoundedRect(Drawing.RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeService.ThemeChanged -= OnThemeChanged;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _drawnIcon?.Dispose();
            // Icon.FromHandle does not take ownership of the HICON.
            if (_hIcon != IntPtr.Zero) Interop.DestroyIcon(_hIcon);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static class Interop
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    /// <summary>Colors for the WinForms menu so it roughly matches the WPF theme.</summary>
    sealed class DarkColors : Forms.ProfessionalColorTable
    {
        // Rebuilt from the theme; the renderer reads these properties on every paint.
        public static Drawing.Color Background { get; private set; } = Drawing.Color.FromArgb(26, 26, 34);
        public static Drawing.Color Hover { get; private set; } = Drawing.Color.FromArgb(48, 48, 60);
        public static Drawing.Color Border { get; private set; } = Drawing.Color.FromArgb(52, 52, 64);
        public static readonly Drawing.Color Text = Drawing.Color.FromArgb(245, 245, 247);
        static Drawing.Color Accent { get; set; } = Drawing.Color.FromArgb(167, 139, 250);

        public static void UpdateFromTheme()
        {
            var tint = ThemeService.Tint;
            var accent = ThemeService.Accent;
            // Same offsets as the original graphite palette (#121218 → 26,26,34 / 48,48,60 / 52,52,64).
            static int C(int v, int add) => Math.Clamp(v + add, 0, 255);
            Background = Drawing.Color.FromArgb(C(tint.R, 8), C(tint.G, 8), C(tint.B, 10));
            Hover = Drawing.Color.FromArgb(C(tint.R, 30), C(tint.G, 30), C(tint.B, 36));
            Border = Drawing.Color.FromArgb(C(tint.R, 34), C(tint.G, 34), C(tint.B, 40));
            Accent = Drawing.Color.FromArgb(accent.R, accent.G, accent.B);
        }

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Background;
        public override Drawing.Color CheckBackground => Accent;
        public override Drawing.Color CheckSelectedBackground => Accent;
        public override Drawing.Color CheckPressedBackground => Accent;
    }
}
