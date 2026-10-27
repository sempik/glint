using System.Text.Encodings.Web;
using System.Text.Json;
using Glint.Core;

namespace Glint.Services;

public sealed class NoteFolder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime Created { get; init; } = DateTime.Now;
}

/// <summary>Index entry for one note. The formatted body lives in its own RTF file.</summary>
public sealed class NoteMeta
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Null: not in any folder (shown under «Все заметки» only).</summary>
    public Guid? FolderId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Plain text, capped, for search and the list preview.</summary>
    public string Text { get; set; } = "";
    public DateTime Created { get; init; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
}

/// <summary>
/// Notes for the Заметки tab, in %AppData%\Glint\notes: one RTF file per note (opens in WordPad too) plus a small
/// index.json with folders, titles and plain text for search. Every write goes through <see cref="AtomicFile"/>.
/// Deleted notes move to notes\deleted and are purged after 30 days, so a slip can be undone.
/// Thread-safe: the command bar searches it from a background thread.
/// </summary>
public sealed class NoteStore
{
    public static NoteStore Shared { get; } = new();

    public static string Dir { get; } = Path.Combine(SettingsStore.DataDir, "notes");
    static string DeletedDir => Path.Combine(Dir, "deleted");
    static string IndexPath => Path.Combine(Dir, "index.json");

    const int MaxIndexedText = 20_000;
    static readonly TimeSpan KeepDeleted = TimeSpan.FromDays(30);
    static readonly TimeSpan RetryLoadAfter = TimeSpan.FromSeconds(3);
    // Every autosave rewrites the index; Cyrillic as \uXXXX escapes would make it several times larger.
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    sealed class IndexFile
    {
        public int Version { get; set; } = 1;
        public List<NoteFolder> Folders { get; set; } = new();
        public List<NoteMeta> Notes { get; set; } = new();
    }

    readonly object _gate = new();
    IndexFile _index = new();
    bool _loaded;
    DateTime _lastLoadAttempt = DateTime.MinValue;

    /// <summary>Raised on the thread that made the change (the UI thread for everything but a failed load).</summary>
    public event EventHandler? Changed;

    public static string BodyPath(Guid id) => Path.Combine(Dir, id.ToString("N") + ".rtf");

    void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            // A locked index is tried again a little later, not on every call: the list asks many times per refresh.
            if (DateTime.UtcNow - _lastLoadAttempt < RetryLoadAfter) return;
            _lastLoadAttempt = DateTime.UtcNow;
            string? json = null;
            try
            {
                if (File.Exists(IndexPath)) json = ReadIndexText();
            }
            catch (Exception ex)
            {
                // Locked by another program or not readable right now. The file itself may be fine, so nothing is
                // written over it (see SaveIndexLocked) until a later read succeeds.
                App.LogError(ex);
                return;
            }
            IndexFile? index = null;
            if (json is not null)
            {
                try
                {
                    index = JsonSerializer.Deserialize<IndexFile>(json);
                }
                catch (Exception ex)
                {
                    // Really damaged. Keep it for a human to look at before the rebuilt index replaces it;
                    // the notes themselves are safe in their RTF files.
                    App.LogError(ex);
                    if (!TryWrite(IndexPath + ".broken", json)) return;
                }
            }
            _loaded = true;
            _index = index ?? new IndexFile();
            _index.Folders ??= new();
            _index.Notes ??= new();
            _index.Folders.RemoveAll(f => f is null);
            _index.Notes.RemoveAll(n => n is null);
            foreach (var n in _index.Notes)
            {
                n.Title ??= "";
                n.Text ??= "";
                if (n.FolderId is { } fid && _index.Folders.All(f => f.Id != fid)) n.FolderId = null;
            }
            RecoverOrphans();
            AtomicFile.CleanupTemp(Dir);
            PurgeDeleted();
        }
    }

    /// <summary>A sharing violation (backup, antivirus, sync) usually passes in a moment.</summary>
    static string ReadIndexText()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(IndexPath);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>An RTF file with no index entry (index lost or damaged) comes back as an untitled note.</summary>
    void RecoverOrphans()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var known = _index.Notes.Select(n => n.Id).ToHashSet();
            var added = false;
            foreach (var file in Directory.EnumerateFiles(Dir, "*.rtf"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) || known.Contains(id)) continue;
                var time = File.GetLastWriteTime(file);
                _index.Notes.Add(new NoteMeta { Id = id, Title = "Восстановленная заметка", Created = time, Modified = time });
                added = true;
            }
            if (added) SaveIndexLocked();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static void PurgeDeleted()
    {
        try
        {
            if (!Directory.Exists(DeletedDir)) return;
            foreach (var f in Directory.EnumerateFiles(DeletedDir))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > KeepDeleted) File.Delete(f);
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ───────────── Queries ─────────────

    public IReadOnlyList<NoteFolder> Folders()
    {
        EnsureLoaded();
        lock (_gate) return _index.Folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public NoteMeta? Find(Guid id)
    {
        EnsureLoaded();
        lock (_gate) return _index.Notes.FirstOrDefault(n => n.Id == id);
    }

    public int CountIn(Guid? folder)
    {
        EnsureLoaded();
        lock (_gate) return folder is null ? _index.Notes.Count : _index.Notes.Count(n => n.FolderId == folder);
    }

    /// <summary>
    /// Newest first. <paramref name="folder"/> null means every note. A search looks through all folders and
    /// needs every word to appear in the title or text, in any order.
    /// </summary>
    public IReadOnlyList<NoteMeta> List(Guid? folder, string? search = null)
    {
        EnsureLoaded();
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_gate)
        {
            IEnumerable<NoteMeta> q = _index.Notes;
            if (words.Length > 0)
                q = q.Where(n => words.All(w => n.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) || n.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
            else if (folder is not null)
                q = q.Where(n => n.FolderId == folder);
            return q.OrderByDescending(n => n.Modified).ToList();
        }
    }

    // ───────────── Notes ─────────────

    /// <summary>A new note, written to disk at once so it exists even if nothing is typed.</summary>
    public NoteMeta Create(Guid? folder, byte[]? rtf = null, string plainText = "")
    {
        EnsureLoaded();
        var meta = new NoteMeta { FolderId = folder };
        Fill(meta, plainText);
        AtomicFile.WriteAllBytes(BodyPath(meta.Id), rtf ?? Array.Empty<byte>());
        lock (_gate)
        {
            _index.Notes.Add(meta);
            SaveIndexLocked();
        }
        RaiseChanged();
        return meta;
    }

    /// <summary>RTF of the note, or null if the file is gone (then it opens empty).</summary>
    public byte[]? LoadBody(Guid id)
    {
        try
        {
            var path = BodyPath(id);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return null;
        }
    }

    /// <summary>Writes the body and refreshes the title/preview. Throws on disk errors so the editor can say so.</summary>
    public void SaveBody(Guid id, byte[] rtf, string plainText)
    {
        EnsureLoaded();
        AtomicFile.WriteAllBytes(BodyPath(id), rtf);
        lock (_gate)
        {
            var meta = _index.Notes.FirstOrDefault(n => n.Id == id);
            if (meta is null)
            {
                // Saved after a delete from elsewhere (the command bar never deletes, but be safe): keep it.
                meta = new NoteMeta { Id = id };
                _index.Notes.Add(meta);
            }
            Fill(meta, plainText);
            meta.Modified = DateTime.Now;
            SaveIndexLocked();
        }
        RaiseChanged();
    }

    static void Fill(NoteMeta meta, string plainText)
    {
        var text = plainText.Replace("\r\n", "\n").Trim();
        meta.Title = TitleOf(text);
        meta.Text = text.Length > MaxIndexedText ? text[..MaxIndexedText] : text;
    }

    /// <summary>First non-empty line without list and checkbox marks, up to 80 characters.</summary>
    public static string TitleOf(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('☐', '☑', '•', '·', '-', '*', '#', '\t', ' ').Trim();
            if (line.Length == 0) continue;
            return line.Length > 80 ? line[..80].TrimEnd() + "…" : line;
        }
        return "";
    }

    public void Move(Guid id, Guid? folder)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var meta = _index.Notes.FirstOrDefault(n => n.Id == id);
            if (meta is null || meta.FolderId == folder) return;
            meta.FolderId = folder;
            SaveIndexLocked();
        }
        RaiseChanged();
    }

    /// <summary>Moves the note to notes\deleted. Returns its entry for <see cref="Restore"/>.</summary>
    public NoteMeta? Delete(Guid id)
    {
        EnsureLoaded();
        NoteMeta? meta;
        lock (_gate)
        {
            meta = _index.Notes.FirstOrDefault(n => n.Id == id);
            if (meta is null) return null;
            _index.Notes.Remove(meta);
            SaveIndexLocked();
        }
        try
        {
            var body = BodyPath(id);
            if (File.Exists(body))
            {
                Directory.CreateDirectory(DeletedDir);
                var target = Path.Combine(DeletedDir, Path.GetFileName(body));
                File.Move(body, target, overwrite: true);
                // Purge counts from the delete, not from the last edit.
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        RaiseChanged();
        return meta;
    }

    public bool Restore(NoteMeta meta)
    {
        EnsureLoaded();
        try
        {
            var deleted = Path.Combine(DeletedDir, Path.GetFileName(BodyPath(meta.Id)));
            if (File.Exists(deleted)) File.Move(deleted, BodyPath(meta.Id), overwrite: true);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return false;
        }
        lock (_gate)
        {
            if (meta.FolderId is { } fid && _index.Folders.All(f => f.Id != fid)) meta.FolderId = null;
            if (_index.Notes.All(n => n.Id != meta.Id)) _index.Notes.Add(meta);
            SaveIndexLocked();
        }
        RaiseChanged();
        return true;
    }

    // ───────────── Folders ─────────────

    public NoteFolder CreateFolder(string name)
    {
        EnsureLoaded();
        NoteFolder folder;
        lock (_gate)
        {
            folder = new NoteFolder { Name = UniqueName(CleanName(name), null) };
            _index.Folders.Add(folder);
            SaveIndexLocked();
        }
        RaiseChanged();
        return folder;
    }

    public void RenameFolder(Guid id, string name)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var folder = _index.Folders.FirstOrDefault(f => f.Id == id);
            var clean = CleanName(name);
            if (folder is null || folder.Name == clean) return;
            folder.Name = UniqueName(clean, id);
            SaveIndexLocked();
        }
        RaiseChanged();
    }

    /// <summary>Removes the folder; its notes stay, outside any folder. Returns how many were moved out.</summary>
    public int DeleteFolder(Guid id)
    {
        EnsureLoaded();
        int moved;
        lock (_gate)
        {
            if (_index.Folders.RemoveAll(f => f.Id == id) == 0) return 0;
            var inside = _index.Notes.Where(n => n.FolderId == id).ToList();
            foreach (var n in inside) n.FolderId = null;
            moved = inside.Count;
            SaveIndexLocked();
        }
        RaiseChanged();
        return moved;
    }

    static string CleanName(string name)
    {
        var n = string.Join(" ", (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (n.Length > 40) n = n[..40].TrimEnd();
        return n.Length == 0 ? "Новая папка" : n;
    }

    string UniqueName(string name, Guid? self)
    {
        var taken = _index.Folders.Where(f => f.Id != self).Select(f => f.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ───────────── Persistence ─────────────

    void SaveIndexLocked()
    {
        // The index on disk couldn't be read yet: an empty one must not replace it.
        if (!_loaded) return;
        try
        {
            AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(_index, JsonOptions));
        }
        catch (Exception ex)
        {
            // The bodies are already safe; the index is rebuilt from them if it never makes it to disk.
            App.LogError(ex);
        }
    }

    static bool TryWrite(string path, string text)
    {
        try
        {
            AtomicFile.WriteAllText(path, text);
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return false;
        }
    }

    /// <summary>
    /// Copies a note body the editor could only open as plain text to "&lt;id&gt;.rtf.broken", before the next
    /// autosave writes over it.
    /// </summary>
    public void KeepBrokenBody(Guid id)
    {
        try
        {
            var path = BodyPath(id);
            if (File.Exists(path)) File.Copy(path, path + ".broken", overwrite: true);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
