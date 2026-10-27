namespace Glint.Core;

/// <summary>Apps pinned to the Home dock; stored in <see cref="AppSettings.DockApps"/>.</summary>
public static class PinnedApps
{
    public static bool IsPinned(string path) =>
        AppServices.Settings.Current.DockApps.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the paths that aren't pinned yet and saves once; returns how many were added.</summary>
    public static int Pin(IEnumerable<string> paths)
    {
        var settings = AppServices.Settings;
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || IsPinned(path)) continue;
            settings.Current.DockApps.Add(path);
            added++;
        }
        if (added > 0) settings.Save();
        return added;
    }

    public static void Unpin(string path)
    {
        var settings = AppServices.Settings;
        if (settings.Current.DockApps.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
            settings.Save();
    }
}
