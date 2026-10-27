using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Glint.Core;

/// <summary>
/// A shadow made of thin rings that fade outward and drift down a little. Glint renders in software, where a
/// DropShadowEffect goes through a shader emulator: re-blurring a panel or the command bar took 100-200 ms and
/// stalled animations and typing. A few outlines look nearly the same and cost almost nothing to redraw.
/// </summary>
public static class SoftShadow
{
    /// <summary>
    /// Fills <paramref name="host"/>, which must cover the same area as the surface it shades, with the rings.
    /// </summary>
    public static void Fill(Panel host, double cornerRadius, int rings, double depth, double strength)
    {
        host.Children.Clear();
        for (var i = 0; i < rings; i++)
        {
            var spread = i + 1.0;
            // Outer rings sit lower, like a blur offset downward; alpha falls off roughly like a gaussian edge.
            var drop = depth * spread / rings;
            var fade = 1 - spread / (rings + 1);
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * strength * fade * fade), 0, 0, 0));
            brush.Freeze();
            host.Children.Add(new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = brush,
                SnapsToDevicePixels = true,
                Margin = new Thickness(-spread, -spread + drop, -spread, -spread - drop),
                CornerRadius = new CornerRadius(cornerRadius + spread),
            });
        }
    }
}
