using System.Runtime.InteropServices;
using Glint.Core;

namespace Glint.CommandBar;

/// <summary>Shallow index of the user's own folders (Desktop, Documents, Downloads, media), rebuilt in the background.</summary>
public sealed class FileIndex : ICommandProvider
{
    sealed record Entry(string Name, string Lower, string FullPath, bool IsFolder);

    const int MaxEntries = 50_000, MaxDepth = 4, MaxResults = 5;

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", "bin", "obj", "__pycache__", ".venv", "venv", "$RECYCLE.BIN",
    };

    static readonly HashSet<string> Elevatable = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".bat", ".cmd", ".msi" };

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static FileIndex Shared { get; } = new();

    volatile Entry[] _entries = Array.Empty<Entry>();
    System.Threading.Timer? _refresh;
    int _scanning;

    /// <summary>Starts background indexing (shortly after startup, then every 15 minutes). Safe to call more than once.</summary>
    public void Start()
    {
        if (_refresh is not null) return;
        // Give app startup a head start; the first walk touches a lot of disk.
        _refresh = new System.Threading.Timer(_ => Scan(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(15));
    }

    void Scan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var list = new List<Entry>(4096);
            foreach (var root in Roots())
            {
                if (list.Count >= MaxEntries) break;
                Walk(new DirectoryInfo(root), 1, list);
            }
            _entries = list.ToArray();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    static IEnumerable<string> Roots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            DownloadsFolder(),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        return candidates
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static void Walk(DirectoryInfo dir, int depth, List<Entry> list)
    {
        var subdirs = new List<DirectoryInfo>();
        try
        {
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                if (list.Count >= MaxEntries) return;
                var attrs = item.Attributes;
                if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (item.Name.StartsWith('.') || item.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                if (item is DirectoryInfo sub)
                {
                    if (SkipDirs.Contains(sub.Name)) continue;
                    list.Add(new Entry(sub.Name, sub.Name.ToLowerInvariant(), sub.FullName, true));
                    // Junctions can loop back on themselves (e.g. legacy "My Music" links).
                    if ((attrs & FileAttributes.ReparsePoint) == 0 && depth < MaxDepth) subdirs.Add(sub);
                }
                else
                {
                    list.Add(new Entry(item.Name, item.Name.ToLowerInvariant(), item.FullName, false));
                }
            }
        }
        catch (Exception)
        {
            // Access denied or a folder vanishing mid-walk: keep what we have.
        }
        foreach (var sub in subdirs) Walk(sub, depth + 1, list);
    }

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length < 2) return Array.Empty<CommandResult>();

        var entries = _entries;
        var hits = new List<(Entry Entry, double Score)>();
        for (var i = 0; i < entries.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var s = Matcher.Score(entries[i].Lower, string.Empty, q, fuzzy: false);
            if (s > 0) hits.Add((entries[i], s));
        }
        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Entry.Name.Length)
            .Take(MaxResults)
            .Select(h => ToResult(h.Entry, h.Score))
            .ToList();
    }

    static CommandResult ToResult(Entry e, double score)
    {
        var path = e.FullPath;
        return new CommandResult
        {
            Title = e.Name,
            Subtitle = Pretty(Path.GetDirectoryName(path)),
            Hint = e.IsFolder ? "Папка" : "Файл",
            Glyph = e.IsFolder ? Glyphs.Folder : Glyphs.Document,
            IconPath = e.IsFolder ? null : path,
            Score = score,
            Path = path,
            Execute = () => ShellRun.Open(path),
            ExecuteElevated = !e.IsFolder && Elevatable.Contains(Path.GetExtension(path))
                ? () => ShellRun.Open(path, elevated: true)
                : null,
        };
    }

    static string Pretty(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return string.Empty;
        return Home.Length > 0 && dir.StartsWith(Home, StringComparison.OrdinalIgnoreCase) ? "~" + dir[Home.Length..] : dir;
    }

    static string DownloadsFolder()
    {
        try
        {
            if (Interop.SHGetKnownFolderPath(Interop.FolderDownloads, 0, IntPtr.Zero, out var path) == 0 && !string.IsNullOrEmpty(path))
                return path;
        }
        catch (Exception)
        {
            // Fall through to the conventional location.
        }
        return Path.Combine(Home, "Downloads");
    }

    static class Interop
    {
        public static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out string path);
    }
}
