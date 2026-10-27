using System.Text;

namespace Glint.Core;

/// <summary>
/// Crash-safe file writes: the data goes to a temp file next to the target, is flushed to disk, and only then
/// replaces the target in one rename. A power cut leaves either the old file or the new one, never half of each.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

    public static void WriteAllBytes(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Unique per write, so two quick saves of the same file can't trip over each other's temp file.
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(flushToDisk: true);
            }
            // Same folder, same volume: MoveFileEx with REPLACE_EXISTING is a single rename on NTFS.
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Removes temp files a crash left behind in a folder this class writes to.</summary>
    public static void CleanupTemp(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                // Only stale ones; a save may be in flight right now.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromMinutes(5)) TryDelete(f);
            }
        }
        catch (Exception)
        {
            // Cleanup is best effort.
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Left for CleanupTemp.
        }
    }
}
