using System.Windows;

namespace Glint.Core;

/// <summary>A connected monitor. Rectangles are in physical pixels of the virtual desktop.</summary>
public sealed record DisplayInfo(string Id, int Number, bool Primary, Int32Rect Bounds, Int32Rect Work);

/// <summary>Connected monitors, for choosing where the pill and the command bar live.</summary>
public static class Displays
{
    /// <summary>All monitors, ordered by their Windows number (the trailing digits of \\.\DISPLAYn).</summary>
    public static IReadOnlyList<DisplayInfo> All()
    {
        var list = new List<DisplayInfo>();
        try
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var b = screen.Bounds;
                var w = screen.WorkingArea;
                if (b.Width <= 0 || b.Height <= 0) continue;
                list.Add(new DisplayInfo(screen.DeviceName ?? "", NumberOf(screen.DeviceName), screen.Primary,
                    new Int32Rect(b.X, b.Y, b.Width, b.Height), new Int32Rect(w.X, w.Y, w.Width, w.Height)));
            }
        }
        catch (Exception)
        {
            // No display information (a session being torn down): the fallback below still gives a rectangle.
        }
        if (list.Count == 0)
        {
            var width = Math.Max(1, (int)SystemParameters.PrimaryScreenWidth);
            var height = Math.Max(1, (int)SystemParameters.PrimaryScreenHeight);
            var rect = new Int32Rect(0, 0, width, height);
            list.Add(new DisplayInfo("", 1, true, rect, rect));
        }
        return list.OrderBy(d => d.Number).ToList();
    }

    /// <summary>The monitor with this id; the primary one when the id is empty or that monitor is not connected.</summary>
    public static DisplayInfo Resolve(string? id)
    {
        var all = All();
        if (!string.IsNullOrEmpty(id))
        {
            var chosen = all.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (chosen is not null) return chosen;
        }
        return all.FirstOrDefault(d => d.Primary) ?? all[0];
    }

    static int NumberOf(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return 0;
        var end = deviceName.Length;
        while (end > 0 && char.IsDigit(deviceName[end - 1])) end--;
        return int.TryParse(deviceName.AsSpan(end), out var n) ? n : 0;
    }
}
