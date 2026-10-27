using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glint.CommandBar;
using Glint.Core;
using Glint.Services;
using Glyphs = Glint.Core.Glyphs;

namespace Glint.Pages;

/// <summary>
/// Заметки: folders, a notes list and a formatted editor (bold, italic, underline, strikethrough, headings, lists,
/// checklists, colours). Autosaves a moment after typing stops, and on note switch, tab switch and exit.
/// The «Напоминания» entry lists upcoming reminders and adds new ones in the command bar's words.
/// </summary>
public partial class NotesPage : UserControl
{
    const string NoteDragFormat = "Glint.Note";
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(900);
    static NotesPage? s_instance;

    enum View { All, Folder, Reminders }

    readonly DispatcherTimer _saveTimer = new() { Interval = SaveDelay };
    readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly DispatcherTimer _reminderTick = new();
    readonly Dictionary<string, Button> _format = new();
    readonly List<(Reminder Reminder, TextBlock When)> _reminderRows = new();

    View _view = View.All;
    Guid? _folder;
    Guid? _noteId;
    bool _dirty;
    bool _loadingNote;
    bool _attached;
    bool _savingSelf;
    NoteMeta? _undoMeta;
    Guid? _renaming;
    Point? _dragStart;
    Guid? _dragNote;

    public NotesPage()
    {
        InitializeComponent();
        s_instance = this;

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RefreshNotes(); };
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); UndoBar.Visibility = Visibility.Collapsed; _undoMeta = null; };
        _reminderTick.Tick += (_, _) => TickReminders();

        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchDebounce.Stop();
            // Reminders aren't searched; typing here means notes, so show them.
            if (_view == View.Reminders && SearchBox.Text.Trim().Length > 0)
            {
                SelectView(View.All, null, keepSearch: true);
                return;
            }
            _searchDebounce.Start();
        };
        SearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; }
        };
        NewFolderButton.Click += (_, _) => NewFolder();
        NewNoteButton.Click += (_, _) => NewNote();
        DeleteNoteButton.Click += (_, _) => DeleteCurrent();
        UndoButton.Click += (_, _) => Undo();

        BuildToolbar();
        Editor.TextChanged += OnEditorTextChanged;
        Editor.SelectionChanged += (_, _) => UpdateFormatState();
        Editor.PreviewKeyDown += OnEditorKeyDown;
        Editor.PreviewMouseLeftButtonUp += OnEditorMouseUp;
        DataObject.AddPastingHandler(Editor, OnPasting);
        PreviewKeyDown += OnPageKeyDown;
        // The page lives as long as the app (see s_instance), so it listens even while another tab is shown.
        ThemeService.ThemeChanged += (_, _) => RefreshInk();

        ReminderInput.TextChanged += (_, _) => PreviewReminder();
        ReminderInput.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; AddReminder(); }
        };
        AddReminderButton.Click += (_, _) => AddReminder();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) =>
        {
            // Leaving the tab is a natural moment to save; countdowns stop with it.
            if (!IsVisible) SaveNow();
            UpdateReminderTick();
        };

        ShowEditorFor(null);
    }

    /// <summary>Writes any unsaved edit; App calls it on exit.</summary>
    public static void FlushAll() => s_instance?.SaveNow();

    // ───────────── Lifetime ─────────────

    // The pill caches pages and re-attaches them, so Loaded/Unloaded can fire many times.
    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        NoteStore.Shared.Changed += OnStoreChanged;
        ReminderService.Shared.Changed += OnRemindersChanged;
        RefreshFolders();
        RefreshNotes();
        if (_view == View.Reminders) RefreshReminders();
        // First visit: open the newest note so the tab isn't a blank wall.
        if (_noteId is null && _view != View.Reminders && NoteStore.Shared.List(_folder).FirstOrDefault() is { } newest) OpenNote(newest.Id, focus: false);
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_attached) return;
        _attached = false;
        SaveNow();
        NoteStore.Shared.Changed -= OnStoreChanged;
        ReminderService.Shared.Changed -= OnRemindersChanged;
        UpdateReminderTick();
    }

    /// <summary>A change made from this page, which refreshes itself; the store's Changed event is for everyone else.</summary>
    T Own<T>(Func<T> change)
    {
        _savingSelf = true;
        try
        {
            return change();
        }
        finally
        {
            _savingSelf = false;
        }
    }

    void Own(Action change) => Own(() => { change(); return true; });

    void OnStoreChanged(object? sender, EventArgs e)
    {
        // Own changes refresh the page directly; anything else (a quick note from the command bar) rebuilds.
        // A folder being renamed would lose its text box to a rebuild.
        if (_savingSelf || _renaming is not null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_attached) return;
            RefreshFolders();
            RefreshNotes();
        });
    }

    void OnRemindersChanged(object? sender, EventArgs e)
    {
        // Mid-rename a rebuild would commit the half-typed name; the counter catches up when the rename ends.
        if (_renaming is null) RefreshFolders();
        if (_view == View.Reminders) RefreshReminders();
    }

    // ───────────── Folders ─────────────

    void RefreshFolders()
    {
        FolderList.Children.Clear();
        FolderList.Children.Add(ListRow(Glyphs.Note, "Все заметки", NoteStore.Shared.CountIn(null).ToString(), _view == View.All, () => SelectView(View.All, null)));
        var reminders = ReminderService.Shared.Count;
        FolderList.Children.Add(ListRow(Glyphs.Clock, "Напоминания", reminders > 0 ? reminders.ToString() : "", _view == View.Reminders, () => SelectView(View.Reminders, null)));

        var folders = NoteStore.Shared.Folders();
        if (folders.Count > 0)
            FolderList.Children.Add(new Border { Height = 1, Margin = new Thickness(6, 6, 6, 6), Background = (Brush)FindResource("DividerBrush") });
        foreach (var f in folders)
        {
            var folder = f;
            if (_renaming == folder.Id)
            {
                FolderList.Children.Add(RenameBox(folder));
                continue;
            }
            var row = ListRow(Glyphs.Folder, folder.Name, NoteStore.Shared.CountIn(folder.Id).ToString(), _view == View.Folder && _folder == folder.Id,
                () => SelectView(View.Folder, folder.Id));
            row.ToolTip = folder.Name;
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { e.Handled = true; StartRename(folder.Id); }
            };

            var rename = new MenuItem { Header = "Переименовать" };
            rename.Click += (_, _) => StartRename(folder.Id);
            var delete = new MenuItem { Header = "Удалить папку" };
            delete.Click += (_, _) => DeleteFolder(folder);
            row.ContextMenu = new ContextMenu { Items = { rename, delete } };

            // Drop a note here to move it.
            row.AllowDrop = true;
            row.DragEnter += OnFolderDragOver;
            row.DragOver += OnFolderDragOver;
            row.Drop += (_, e) =>
            {
                e.Handled = true;
                if (e.Data.GetData(NoteDragFormat) is string s && Guid.TryParse(s, out var id)) MoveNote(id, folder.Id);
            };
            FolderList.Children.Add(row);
        }
    }

    static void OnFolderDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(NoteDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    TextBox RenameBox(NoteFolder folder)
    {
        var box = new TextBox { Text = folder.Name, Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 5, 8, 5), MaxLength = 40 };
        var done = false;
        void Commit(bool keep)
        {
            if (done) return;
            done = true;
            _renaming = null;
            if (keep && box.Text.Trim().Length > 0) Own(() => NoteStore.Shared.RenameFolder(folder.Id, box.Text));
            Dispatcher.BeginInvoke(() => { RefreshFolders(); RefreshNotes(); });
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Commit(false); }
        };
        box.LostKeyboardFocus += (_, _) => Commit(true);
        box.Loaded += (_, _) =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        };
        return box;
    }

    void StartRename(Guid id)
    {
        _renaming = id;
        RefreshFolders();
    }

    void NewFolder()
    {
        try
        {
            var folder = Own(() => NoteStore.Shared.CreateFolder("Новая папка"));
            _renaming = folder.Id;
            SelectView(View.Folder, folder.Id);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void DeleteFolder(NoteFolder folder)
    {
        var moved = Own(() => NoteStore.Shared.DeleteFolder(folder.Id));
        if (_view == View.Folder && _folder == folder.Id) SelectView(View.All, null);
        else { RefreshFolders(); RefreshNotes(); }
        ShowStatus(moved > 0 ? $"Папка удалена, заметки ({moved}) остались во «Всех заметках»" : "Папка удалена");
    }

    void SelectView(View view, Guid? folder, bool keepSearch = false)
    {
        var reminders = view == View.Reminders;
        // A view that would close the open note waits until its edits are saved. The reminders view only hides it.
        var keepsNote = reminders || (_noteId is { } open && NoteStore.Shared.List(folder).Any(n => n.Id == open));
        if (keepsNote) SaveNow();
        else if (!CanLeaveNote())
        {
            // A folder just created still has to show up.
            RefreshFolders();
            return;
        }
        _view = view;
        _folder = folder;
        RemindersView.Visibility = reminders ? Visibility.Visible : Visibility.Collapsed;
        NotesColumn.Visibility = reminders ? Visibility.Collapsed : Visibility.Visible;
        EditorColumn.Visibility = reminders ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(SearchBox.Text) && !reminders && !keepSearch) SearchBox.Clear();
        RefreshFolders();
        // No auto-focus on the reminder box: a focused text box keeps the panel open until the next click elsewhere.
        if (reminders)
        {
            RefreshReminders();
        }
        else
        {
            RefreshNotes();
            // Keep the open note if it belongs here, else open this folder's newest.
            var list = NoteStore.Shared.List(folder);
            if (_noteId is not { } id || list.All(n => n.Id != id)) OpenNote(list.FirstOrDefault()?.Id, focus: false);
        }
        UpdateReminderTick();
    }

    /// <summary>Opens the «Напоминания» view; Главная's "все напоминания" link leads here.</summary>
    public void ShowReminders() => SelectView(View.Reminders, null);

    /// <summary>Opens a note by id (from the command bar or a flash).</summary>
    public void ShowNote(Guid id)
    {
        var meta = NoteStore.Shared.Find(id);
        if (meta is null) return;
        if (_view == View.Reminders || (_view == View.Folder && _folder != meta.FolderId)) SelectView(View.All, null);
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Clear();
        OpenNote(id, focus: true);
    }

    // ───────────── Notes list ─────────────

    void RefreshNotes()
    {
        if (_view == View.Reminders) return;
        var search = SearchBox.Text.Trim();
        var notes = NoteStore.Shared.List(_folder, search);
        if (search.Length > 0) ListTitle.Text = $"НАЙДЕНО: {notes.Count}";
        else if (_view == View.Folder && NoteStore.Shared.Folders().FirstOrDefault(f => f.Id == _folder) is { } folder) ListTitle.Text = folder.Name.ToUpperInvariant();
        else ListTitle.Text = "ВСЕ ЗАМЕТКИ";

        NoteList.Children.Clear();
        foreach (var n in notes) NoteList.Children.Add(NoteRow(n));
        ListEmpty.Text = search.Length > 0 ? "Ничего не найдено" : "Здесь пока пусто. Нажмите «+», чтобы создать заметку";
        ListEmpty.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    Border NoteRow(NoteMeta n)
    {
        var title = new TextBlock
        {
            Text = n.Title.Length > 0 ? n.Title : "Новая заметка",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
        };
        var preview = new TextBlock
        {
            Style = (Style)FindResource("MutedText"),
            Text = Preview(n),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var row = new Border
        {
            Child = new StackPanel { Children = { title, preview } },
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            Tag = n.Id,
        };
        Highlight(row, n.Id == _noteId);
        HoverFill(row, () => n.Id == _noteId);
        var id = n.Id;
        row.MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragNote = id; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            _dragStart = null;
            if (id != _noteId) OpenNote(id, focus: false);
            e.Handled = true;
        };
        row.MouseMove += (_, e) => TryStartDrag(row, e);
        row.ContextMenu = NoteMenu(n);
        return row;
    }

    string Preview(NoteMeta n)
    {
        var body = n.Text;
        var firstBreak = body.IndexOf('\n');
        var rest = firstBreak >= 0 ? body[(firstBreak + 1)..] : "";
        rest = string.Join(" ", rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var date = n.Modified.Date == DateTime.Today ? n.Modified.ToString("HH:mm") : n.Modified.ToString("dd.MM");
        // In search results, say which folder it lives in.
        var folder = SearchBox.Text.Length > 0 && n.FolderId is { } fid ? NoteStore.Shared.Folders().FirstOrDefault(f => f.Id == fid)?.Name : null;
        var parts = new[] { date, folder, rest.Length > 0 ? rest : null }.Where(p => !string.IsNullOrEmpty(p));
        return string.Join(" · ", parts);
    }

    ContextMenu NoteMenu(NoteMeta n)
    {
        var menu = new ContextMenu();
        var id = n.Id;
        foreach (var f in NoteStore.Shared.Folders().Where(f => f.Id != n.FolderId))
        {
            var target = f.Id;
            var item = new MenuItem { Header = $"В папку «{f.Name}»" };
            item.Click += (_, _) => MoveNote(id, target);
            menu.Items.Add(item);
        }
        if (n.FolderId is not null)
        {
            var item = new MenuItem { Header = "Убрать из папки" };
            item.Click += (_, _) => MoveNote(id, null);
            menu.Items.Add(item);
        }
        var delete = new MenuItem { Header = "Удалить" };
        delete.Click += (_, _) =>
        {
            if (id != _noteId) OpenNote(id, focus: false);
            // The open note couldn't be saved and stayed: don't delete it in place of this one.
            if (id == _noteId) DeleteCurrent();
        };
        menu.Items.Add(delete);
        return menu;
    }

    void TryStartDrag(Border row, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragNote is not { } id || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        _dragNote = null;
        try
        {
            SaveNow();
            DragDrop.DoDragDrop(row, new DataObject(NoteDragFormat, id.ToString()), DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void MoveNote(Guid id, Guid? folder)
    {
        Own(() => NoteStore.Shared.Move(id, folder));
        RefreshFolders();
        RefreshNotes();
        // Moved out of the folder on screen: the editor follows the list.
        if (_noteId == id && _view == View.Folder && folder != _folder) OpenNote(NoteStore.Shared.List(_folder).FirstOrDefault()?.Id, focus: false);
        var name = folder is null ? "«Все заметки»" : $"«{NoteStore.Shared.Folders().FirstOrDefault(f => f.Id == folder)?.Name}»";
        ShowStatus($"Перенесено в {name}");
    }

    void Highlight(Border row, bool selected)
    {
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
        else row.Background = Brushes.Transparent;
    }

    void HoverFill(Border row, Func<bool> isSelected)
    {
        row.MouseEnter += (_, _) => { if (!isSelected()) row.SetResourceReference(Border.BackgroundProperty, "CardBrush"); };
        row.MouseLeave += (_, _) => Highlight(row, isSelected());
    }

    Border ListRow(string glyph, string text, string count, bool selected, Action onClick)
    {
        var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, selected ? "AccentBrush" : "MutedBrush");
        var label = new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var counter = new TextBlock { Style = (Style)FindResource("MutedText"), Text = count, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 1);
        Grid.SetColumn(counter, 2);
        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(counter);

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
        };
        Highlight(row, selected);
        HoverFill(row, () => selected);
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return row;
    }

    // ───────────── Editor ─────────────

    void ShowEditorFor(Guid? id)
    {
        var has = id is not null;
        Editor.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        Toolbar.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        DeleteNoteButton.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        StatusText.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        EditorEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        UpdatePlaceholder();
    }

    void OpenNote(Guid? id, bool focus)
    {
        if (!CanLeaveNote()) return;
        _noteId = id;
        _loadingNote = true;
        var broken = false;
        try
        {
            var doc = NotesDocument.Create();
            if (id is { } noteId && NoteStore.Shared.LoadBody(noteId) is { Length: > 0 } body && !NotesDocument.Load(doc, body))
            {
                // The next autosave rewrites the file from what the editor shows; keep the original first.
                NoteStore.Shared.KeepBrokenBody(noteId);
                broken = true;
            }
            // A new document also resets undo, so Ctrl+Z can't reach into the previous note.
            Editor.Document = doc;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Editor.Document = NotesDocument.Create();
        }
        finally
        {
            _loadingNote = false;
        }
        _dirty = false;
        _saveTimer.Stop();
        ShowEditorFor(id);
        if (broken)
            StatusText.Text = "Файл повреждён: показан текст, оригинал — в .rtf.broken";
        else if (id is { } current && NoteStore.Shared.Find(current) is { } meta)
            StatusText.Text = $"Изменено {ReminderService.Describe(meta.Modified, DateTime.Now)}";

        foreach (var row in NoteList.Children.OfType<Border>())
            Highlight(row, row.Tag is Guid g && g == id);

        if (focus && id is not null)
        {
            Editor.Focus();
            Keyboard.Focus(Editor);
            Editor.CaretPosition = Editor.Document.ContentEnd;
        }
        UpdateFormatState();
    }

    void NewNote()
    {
        if (!CanLeaveNote()) return;
        if (_view == View.Reminders) SelectView(View.All, null);
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        try
        {
            var meta = Own(() => NoteStore.Shared.Create(_view == View.Folder ? _folder : null));
            RefreshFolders();
            RefreshNotes();
            OpenNote(meta.Id, focus: true);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowStatus("Не удалось создать заметку");
        }
    }

    void DeleteCurrent()
    {
        // Unsaved edits would be missing from «Вернуть»; the file is likely locked for the move as well.
        if (_noteId is not { } id || !CanLeaveNote()) return;
        var index = NoteList.Children.OfType<Border>().ToList().FindIndex(r => r.Tag is Guid g && g == id);
        var meta = Own(() => NoteStore.Shared.Delete(id));
        _noteId = null;
        RefreshFolders();
        RefreshNotes();
        // Open the neighbour, like a mail client.
        var rows = NoteList.Children.OfType<Border>().ToList();
        var next = rows.Count == 0 ? null : rows[Math.Clamp(index, 0, rows.Count - 1)].Tag as Guid?;
        OpenNote(next, focus: false);

        if (meta is null) return;
        _undoMeta = meta;
        UndoText.Text = meta.Title.Length > 0 ? $"«{Shorten(meta.Title, 26)}» удалена" : "Заметка удалена";
        UndoBar.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    void Undo()
    {
        _undoTimer.Stop();
        UndoBar.Visibility = Visibility.Collapsed;
        if (_undoMeta is not { } meta) return;
        _undoMeta = null;
        if (!Own(() => NoteStore.Shared.Restore(meta)))
        {
            ShowStatus("Не удалось вернуть заметку");
            return;
        }
        RefreshFolders();
        RefreshNotes();
        OpenNote(meta.Id, focus: false);
    }

    static string Shorten(string s, int max) => s.Length > max ? s[..max].TrimEnd() + "…" : s;

    void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
        if (_loadingNote || _noteId is null) return;
        if (!_dirty) StatusText.Text = "Изменено…";
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void UpdatePlaceholder()
    {
        if (EditorPlaceholder is null) return;
        var doc = Editor.Document;
        // Cheap emptiness check: one empty paragraph.
        var empty = doc.Blocks.Count == 0 || (doc.Blocks.Count == 1 && doc.Blocks.FirstBlock is Paragraph p && p.Inlines.Count == 0);
        EditorPlaceholder.Visibility = empty && _noteId is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Writes the open note if it has unsaved edits. False: the write failed and the edits exist only in the editor.</summary>
    bool SaveNow()
    {
        _saveTimer.Stop();
        if (!_dirty || _noteId is not { } id) return true;
        try
        {
            var rtf = NotesDocument.Save(Editor.Document);
            var text = NotesDocument.PlainText(Editor.Document);
            _savingSelf = true;
            try
            {
                NoteStore.Shared.SaveBody(id, rtf, text);
            }
            finally
            {
                _savingSelf = false;
            }
            _dirty = false;
            StatusText.Text = $"Сохранено в {DateTime.Now:HH:mm}";
            UpdateCurrentRow(id);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            StatusText.Text = "Не удалось сохранить, попробую ещё раз";
            // Keep it dirty and try again shortly (a sync tool may hold the file for a moment).
            _saveTimer.Interval = TimeSpan.FromSeconds(5);
            _saveTimer.Start();
            return false;
        }
        _saveTimer.Interval = SaveDelay;
        return true;
    }

    /// <summary>
    /// Saves before the editor shows something else. While the write keeps failing the note stays open, since
    /// swapping the document would throw the unsaved edits away.
    /// </summary>
    bool CanLeaveNote()
    {
        if (SaveNow()) return true;
        ShowStatus("Не удалось сохранить, заметка пока остаётся открытой");
        return false;
    }

    /// <summary>After an autosave: refresh the open note's row in place (rebuilding would reset hover and scroll).</summary>
    void UpdateCurrentRow(Guid id)
    {
        if (NoteStore.Shared.Find(id) is not { } meta) return;
        var row = NoteList.Children.OfType<Border>().FirstOrDefault(r => r.Tag is Guid g && g == id);
        if (row?.Child is StackPanel { Children.Count: 2 } panel && panel.Children[0] is TextBlock title && panel.Children[1] is TextBlock preview)
        {
            title.Text = meta.Title.Length > 0 ? meta.Title : "Новая заметка";
            preview.Text = Preview(meta);
            // The newest note leads the list.
            if (NoteList.Children.IndexOf(row) > 0 && SearchBox.Text.Length == 0)
            {
                NoteList.Children.Remove(row);
                NoteList.Children.Insert(0, row);
            }
        }
        else
        {
            RefreshNotes();
        }
    }

    void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    // ───────────── Formatting ─────────────

    void BuildToolbar()
    {
        AddFormat("bold", "B", "Жирный (Ctrl+B)", () => EditingCommands.ToggleBold.Execute(null, Editor), t => t.FontWeight = FontWeights.Bold);
        AddFormat("italic", "I", "Курсив (Ctrl+I)", () => EditingCommands.ToggleItalic.Execute(null, Editor), t => t.FontStyle = FontStyles.Italic);
        AddFormat("underline", "U", "Подчёркнутый (Ctrl+U)", () => ToggleDecoration(TextDecorationLocation.Underline), t => t.TextDecorations = TextDecorations.Underline);
        AddFormat("strike", "S", "Зачёркнутый (Ctrl+Shift+X)", () => ToggleDecoration(TextDecorationLocation.Strikethrough), t => t.TextDecorations = TextDecorations.Strikethrough);
        AddSpacer();
        AddFormat("h1", "H1", "Заголовок (Ctrl+1)", () => ToggleHeading(NotesDocument.H1Size), t => t.FontWeight = FontWeights.SemiBold);
        AddFormat("h2", "H2", "Подзаголовок (Ctrl+2)", () => ToggleHeading(NotesDocument.H2Size), t => t.FontWeight = FontWeights.SemiBold);
        AddSpacer();
        AddFormat("bullets", "•", "Маркированный список (Ctrl+Shift+L)", () => EditingCommands.ToggleBullets.Execute(null, Editor), t => t.FontSize = 16);
        AddFormat("numbers", "1.", "Нумерованный список", () => EditingCommands.ToggleNumbering.Execute(null, Editor), null);
        AddFormat("check", NotesDocument.Checked, "Чек-лист (Ctrl+Shift+C); клик по квадратику отмечает пункт", ToggleChecklist, null);
        AddSpacer();
        var color = AddFormat("color", "A", "Цвет текста", () => { }, t =>
        {
            t.FontWeight = FontWeights.SemiBold;
            t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        });
        color.ContextMenu = ColorMenu();
        color.Click += (_, _) =>
        {
            color.ContextMenu.PlacementTarget = color;
            color.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            color.ContextMenu.IsOpen = true;
        };
    }

    Button AddFormat(string key, string label, string tip, Action action, Action<TextBlock>? style)
    {
        var text = new TextBlock { Text = label, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        style?.Invoke(text);
        var button = new Button { Style = (Style)FindResource("FormatButton"), Content = text, ToolTip = tip };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            Editor.Focus();
            UpdateFormatState();
        };
        _format[key] = button;
        Toolbar.Children.Add(button);
        return button;
    }

    void AddSpacer() => Toolbar.Children.Add(new Border { Width = 1, Height = 16, Margin = new Thickness(3, 0, 5, 0), Background = (Brush)FindResource("DividerBrush"), VerticalAlignment = VerticalAlignment.Center });

    ContextMenu ColorMenu()
    {
        var menu = new ContextMenu();
        void Add(string name, Func<Color?> color)
        {
            var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var item = new MenuItem { Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = name } } } };
            menu.Opened += (_, _) =>
            {
                // The accents follow the current theme.
                if (color() is { } c) swatch.Background = new SolidColorBrush(c);
                else swatch.SetResourceReference(Border.BackgroundProperty, "TextBrush");
            };
            item.Click += (_, _) =>
            {
                ApplyColor(color());
                Editor.Focus();
            };
            menu.Items.Add(item);
        }
        Add("Акцент", () => ThemeService.Accent);
        Add("Второй акцент", () => ThemeService.Accent2);
        Add("Красный", () => Color.FromRgb(0xF8, 0x71, 0x71));
        Add("Зелёный", () => Color.FromRgb(0x4A, 0xDE, 0x80));
        Add("Жёлтый", () => Color.FromRgb(0xFB, 0xBF, 0x24));
        Add("Обычный", () => null);
        return menu;
    }

    void ApplyColor(Color? color)
    {
        // "Обычный" writes the theme's text colour (a caret can only carry a colour, not "none" to type in);
        // NotesDocument.Normalize strips it again on load and on a theme switch, so the text keeps following light/dark.
        Brush brush = color is { } c ? new SolidColorBrush(c) : new SolidColorBrush(ThemeService.Text);
        brush.Freeze();
        Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
    }

    /// <summary>After a theme switch: «Обычный» text still holds the old theme's ink, dark on dark or light on light.</summary>
    void RefreshInk()
    {
        var wasLoading = _loadingNote;
        // Not an edit, so not a reason to save.
        _loadingNote = true;
        try
        {
            NotesDocument.Normalize(Editor.Document);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            _loadingNote = wasLoading;
        }
    }

    void ToggleDecoration(TextDecorationLocation location)
    {
        var sel = Editor.Selection;
        var current = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var has = current?.Any(d => d.Location == location) == true;
        var result = new TextDecorationCollection();
        // Keep the other decoration where the selection agrees on it.
        if (current is not null)
            foreach (var d in current.Where(d => d.Location != location)) result.Add(d);
        if (!has)
            foreach (var d in location == TextDecorationLocation.Underline ? TextDecorations.Underline : TextDecorations.Strikethrough) result.Add(d);
        result.Freeze();
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, result);
    }

    void ToggleHeading(double size)
    {
        var paragraphs = NotesDocument.ParagraphsIn(Editor.Document, Editor.Selection.Start, Editor.Selection.End);
        if (paragraphs.Count == 0) return;
        var already = paragraphs.All(p => Math.Abs(HeadingSize(p) - size) < 0.5);
        foreach (var p in paragraphs)
        {
            var range = new TextRange(p.ContentStart, p.ContentEnd);
            if (already)
            {
                range.ApplyPropertyValue(TextElement.FontSizeProperty, NotesDocument.BodySize);
                range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
                p.FontSize = NotesDocument.BodySize;
                p.FontWeight = FontWeights.Normal;
            }
            else
            {
                range.ApplyPropertyValue(TextElement.FontSizeProperty, size);
                range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
                // Empty paragraphs have no runs to carry it; the paragraph itself does.
                p.FontSize = size;
                p.FontWeight = FontWeights.SemiBold;
            }
        }
    }

    static double HeadingSize(Paragraph p)
    {
        var value = new TextRange(p.ContentStart, p.ContentEnd).GetPropertyValue(TextElement.FontSizeProperty);
        return value is double d ? d : p.FontSize;
    }

    void ToggleChecklist()
    {
        var paragraphs = NotesDocument.ParagraphsIn(Editor.Document, Editor.Selection.Start, Editor.Selection.End);
        if (paragraphs.Count == 0) return;
        var all = paragraphs.All(NotesDocument.IsChecklist);
        foreach (var p in paragraphs)
        {
            if (all)
            {
                // Remove the box and the space after it.
                if (NotesDocument.FirstChar(p) is not { } box) continue;
                var end = box.End.GetNextInsertionPosition(LogicalDirection.Forward);
                var cut = end is not null && new TextRange(box.End, end).Text == " " ? new TextRange(box.Start, end) : box;
                cut.Text = string.Empty;
            }
            else if (!NotesDocument.IsChecklist(p))
            {
                p.ContentStart.GetInsertionPosition(LogicalDirection.Forward).InsertTextInRun(NotesDocument.Unchecked + " ");
            }
        }
    }

    /// <summary>A click on a ☐ / ☑ at the start of a paragraph ticks it, crossing the item out.</summary>
    void OnEditorMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!Editor.Selection.IsEmpty) return;
        var point = e.GetPosition(Editor);
        var pos = Editor.GetPositionFromPoint(point, snapToText: false);
        if (pos?.Paragraph is not { } p || NotesDocument.FirstChar(p) is not { } box || box.Text is not (NotesDocument.Unchecked or NotesDocument.Checked)) return;
        if (pos.CompareTo(box.Start) < 0 || pos.CompareTo(box.End) > 0) return;
        // Only on the box itself: a click just after it places the caret as usual.
        var left = box.Start.GetCharacterRect(LogicalDirection.Forward);
        var right = box.End.GetCharacterRect(LogicalDirection.Backward);
        if (point.X < left.Left - 1 || point.X > right.Left + 1) return;
        try
        {
            var check = box.Text == NotesDocument.Unchecked;
            box.Text = check ? NotesDocument.Checked : NotesDocument.Unchecked;
            if (NotesDocument.FirstChar(p) is { } fresh)
            {
                var rest = new TextRange(fresh.End, p.ContentEnd);
                var decorations = check ? TextDecorations.Strikethrough : new TextDecorationCollection();
                rest.ApplyPropertyValue(Inline.TextDecorationsProperty, decorations);
                // Unticked text goes back to the editor's own colour, which follows the theme.
                if (check) rest.ApplyPropertyValue(TextElement.ForegroundProperty, (Brush)FindResource("MutedBrush"));
                else NotesDocument.ClearForeground(p);
                // Leave the caret after the item, not inside the box.
                Editor.CaretPosition = p.ContentEnd;
            }
            e.Handled = true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (ctrl && !shift && e.Key is Key.D1 or Key.NumPad1) { ToggleHeading(NotesDocument.H1Size); e.Handled = true; }
        else if (ctrl && !shift && e.Key is Key.D2 or Key.NumPad2) { ToggleHeading(NotesDocument.H2Size); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.X) { ToggleDecoration(TextDecorationLocation.Strikethrough); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.C) { ToggleChecklist(); e.Handled = true; }
        else if (ctrl && e.Key == Key.U) { ToggleDecoration(TextDecorationLocation.Underline); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { _dirty = _dirty || _noteId is not null; SaveNow(); e.Handled = true; }
        else if (e.Key == Key.Enter && !shift && !ctrl) e.Handled = ContinueChecklist();
        if (e.Handled) UpdateFormatState();
    }

    /// <summary>Enter in a checklist item starts the next one; Enter on an empty item ends the list.</summary>
    bool ContinueChecklist()
    {
        var caret = Editor.CaretPosition;
        if (caret.Paragraph is not { } p || !NotesDocument.IsChecklist(p) || NotesDocument.FirstChar(p) is not { } box) return false;
        // Before the box itself: a plain line break above.
        if (caret.CompareTo(box.End) < 0) return false;
        var text = new TextRange(box.End, p.ContentEnd).Text.Trim();
        if (text.Length == 0)
        {
            new TextRange(box.Start, p.ContentEnd).Text = string.Empty;
            return true;
        }
        if (!Editor.Selection.IsEmpty) Editor.Selection.Text = string.Empty;
        var next = Editor.CaretPosition.InsertParagraphBreak();
        if (next.Paragraph is not { } np) return true;
        np.ContentStart.GetInsertionPosition(LogicalDirection.Forward).InsertTextInRun(NotesDocument.Unchecked + " ");
        // A new item is open, whatever the previous one looked like.
        if (NotesDocument.FirstChar(np) is { } nb)
        {
            var rest = new TextRange(nb.End, np.ContentEnd);
            rest.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
            var after = nb.End.GetNextInsertionPosition(LogicalDirection.Forward) ?? np.ContentEnd;
            Editor.CaretPosition = after;
        }
        return true;
    }

    void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.N) { e.Handled = true; NewNote(); }
        else if (ctrl && e.Key == Key.F) { e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); }
    }

    void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.SourceDataObject;
        var hasText = data.GetDataPresent(DataFormats.Rtf) || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);
        if (!hasText)
        {
            // Pictures would not survive the RTF file, so don't pretend to keep them.
            e.CancelCommand();
            ShowStatus("Картинки в заметки пока не вставляются");
            return;
        }
        // Pasted RTF brings its own black text and white page; clean it once the paste has landed.
        if (data.GetDataPresent(DataFormats.Rtf))
            Dispatcher.BeginInvoke(() => NotesDocument.Normalize(Editor.Document), DispatcherPriority.Background);
    }

    void UpdateFormatState()
    {
        if (_format.Count == 0) return;
        var sel = Editor.Selection;
        var weight = sel.GetPropertyValue(TextElement.FontWeightProperty);
        var style = sel.GetPropertyValue(TextElement.FontStyleProperty);
        var decorations = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var size = sel.GetPropertyValue(TextElement.FontSizeProperty);
        SetActive("bold", weight is FontWeight w && w.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight());
        SetActive("italic", style is FontStyle s && s == FontStyles.Italic);
        SetActive("underline", decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true);
        SetActive("strike", decorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true);
        SetActive("h1", size is double d1 && Math.Abs(d1 - NotesDocument.H1Size) < 0.5);
        SetActive("h2", size is double d2 && Math.Abs(d2 - NotesDocument.H2Size) < 0.5);
        var paragraph = sel.Start.Paragraph;
        SetActive("bullets", paragraph?.Parent is ListItem { List.MarkerStyle: not TextMarkerStyle.Decimal });
        SetActive("numbers", paragraph?.Parent is ListItem { List.MarkerStyle: TextMarkerStyle.Decimal });
        SetActive("check", paragraph is not null && NotesDocument.IsChecklist(paragraph));
    }

    void SetActive(string key, bool active)
    {
        if (!_format.TryGetValue(key, out var button)) return;
        if (active) button.SetResourceReference(BackgroundProperty, "CardHoverBrush");
        else button.Background = Brushes.Transparent;
    }

    // ───────────── Reminders ─────────────

    void RefreshReminders()
    {
        ReminderList.Children.Clear();
        _reminderRows.Clear();
        var items = ReminderService.Shared.Snapshot();
        foreach (var r in items)
        {
            var row = ReminderRows.Build(this, r, compact: false, out var when);
            _reminderRows.Add((r, when));
            ReminderList.Children.Add(row);
        }
        RemindersEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateReminderTick();
    }

    void TickReminders()
    {
        var now = DateTime.Now;
        foreach (var (r, when) in _reminderRows)
        {
            var text = ReminderService.DescribeShort(r, now);
            if (when.Text != text) when.Text = text;
        }
        UpdateReminderTick();
    }

    void UpdateReminderTick()
    {
        var run = IsVisible && _attached && _view == View.Reminders && _reminderRows.Count > 0;
        if (!run)
        {
            _reminderTick.Stop();
            return;
        }
        var interval = ReminderRows.RefreshInterval(_reminderRows.Select(x => x.Reminder));
        if (_reminderTick.Interval != interval) _reminderTick.Interval = interval;
        if (!_reminderTick.IsEnabled) _reminderTick.Start();
    }

    /// <summary>Same words as the command bar; "напомни" in front is optional here.</summary>
    static TimerProvider.Request? ParseReminder(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        // Runs on every keystroke; a parser slip must not reach the dispatcher once per key.
        try
        {
            return TimerProvider.Parse(t, DateTime.Now) ?? TimerProvider.Parse("напомни " + t, DateTime.Now);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return null;
        }
    }

    void PreviewReminder()
    {
        var text = ReminderInput.Text;
        ReminderPlaceholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Trim().Length == 0)
        {
            ReminderPreview.Text = "Через 10 минут…, в 18:30…, завтра в 9…, в пятницу в 7 вечера…, 25.12 в 10:00… — или таймер: «25 мин»";
            ReminderPreview.ClearValue(TextBlock.ForegroundProperty);
            return;
        }
        if (ParseReminder(text) is { } r)
        {
            var when = r.Absolute ? ReminderService.Describe(r.Due, DateTime.Now) : $"через {ReminderService.FormatDuration(r.Delay)} ({ReminderService.Describe(r.Due, DateTime.Now)})";
            ReminderPreview.Text = r.Text is null ? $"Сработает {when}" : $"«{r.Text}» — {when}";
            ReminderPreview.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        }
        else
        {
            ReminderPreview.Text = "Непонятно, когда напомнить. Добавьте время: «через 20 минут», «в 18:30», «завтра в 9»";
            ReminderPreview.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    void AddReminder()
    {
        if (ParseReminder(ReminderInput.Text) is not { } r)
        {
            PreviewReminder();
            return;
        }
        try
        {
            TimerProvider.Start(r);
            ReminderInput.Clear();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
