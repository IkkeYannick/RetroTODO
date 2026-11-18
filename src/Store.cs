using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroTodo;

/// <summary>All todos: the local list (saved in todos.json) plus live-linked project files.</summary>
public sealed class Store
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Undo step: the local list as JSON, or the linked file's text, plus pins and today marks.</summary>
    sealed record Snap(string Label, string? LocalJson, string? FilePath, string? FileText,
        string[] Pinned, KeyValuePair<string, string>[] Today);

    public readonly Section Local = new() { Id = "local", Title = "LOCAL" };
    public readonly List<Section> Linked = new();
    public readonly HashSet<string> Pinned = new();
    public readonly HashSet<string> Collapsed = new();
    public readonly Dictionary<string, string> TodayMarks = new();
    public Settings Settings = new();
    public string? LastError;
    public event Action? Changed;

    readonly List<Snap> undo = new();
    readonly Dictionary<Section, FileSystemWatcher> watchers = new();
    readonly HashSet<Section> dirty = new();
    readonly System.Windows.Forms.Timer reload = new() { Interval = 250 };
    ISynchronizeInvoke? sync;
    int busyTicks;

    Store() => reload.Tick += (_, _) => ReloadDirty();

    public IEnumerable<Section> Sections
    {
        get
        {
            yield return Local;
            foreach (var s in Linked) yield return s;
        }
    }

    public double TotalProgress()
    {
        var tops = Sections.SelectMany(s => s.Items).ToList();
        return tops.Count == 0 ? 0 : tops.Average(t => t.Progress());
    }

    // ---------- today ----------

    public bool IsMarkedToday(string key) => TodayMarks.TryGetValue(key, out var d) && d == Dates.TodayKey;

    /// <summary>On the Today list: not done, and marked for today or due today/overdue.</summary>
    public bool IsToday(TodoItem it, DateTime now) => !it.Complete() && (IsMarkedToday(it.Id) || Dates.DueByToday(it, now));

    /// <summary>Today's todos: overdue/timed first (by time), then whole-day, then manual marks.</summary>
    public List<TodoItem> TodayItems(DateTime now) => Sections
        .SelectMany(s => s.All())
        .Where(i => IsToday(i, now))
        .OrderBy(i => Dates.TryParseStored(i.Due, out var d, out var ht) ? (ht ? d : d.Date.AddHours(23.99)) : DateTime.MaxValue)
        .ToList();

    // ---------- persistence ----------

    public static Store Load()
    {
        var st = new Store();
        StoreData? d = null;
        try
        {
            if (File.Exists(Paths.Data))
                d = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(Paths.Data), Opts);
        }
        catch
        {
            // Never silently lose data: keep a copy of the unreadable file.
            try { File.Copy(Paths.Data, Paths.Data + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
        }
        d ??= new StoreData();
        st.Local.Items = d.Todos ?? new();
        st.Local.Wire();
        st.Settings = d.Settings ?? new();
        foreach (var p in d.Pinned ?? new()) st.Pinned.Add(p);
        foreach (var c in d.Collapsed ?? new()) st.Collapsed.Add(c);
        string today = Dates.TodayKey;
        foreach (var kv in d.Today ?? new())
            if (kv.Value == today) st.TodayMarks[kv.Key] = kv.Value;
        foreach (var path in d.Links ?? new())
        {
            var s = new Section { FilePath = path, Id = Sources.LinkId(path), Title = Sources.DefaultTitle(path) };
            if (!Sources.Load(s)) st.dirty.Add(s);
            st.Linked.Add(s);
        }
        return st;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Dir);
            string today = Dates.TodayKey;
            var d = new StoreData
            {
                Todos = Local.Items,
                Links = Linked.Select(s => s.FilePath!).ToList(),
                Pinned = Pinned.ToList(),
                Collapsed = Collapsed.ToList(),
                Today = TodayMarks.Where(kv => kv.Value == today).ToDictionary(kv => kv.Key, kv => kv.Value),
                Settings = Settings,
            };
            string tmp = Paths.Data + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, Opts));
            File.Move(tmp, Paths.Data, true);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = "SAVE FAILED: " + ex.Message;
        }
    }

    public void Touch(bool save = true)
    {
        if (save) Save();
        Changed?.Invoke();
    }

    // ---------- undo ----------

    void Snapshot(string label, Section s)
    {
        string? local = s.IsLinked ? null : JsonSerializer.Serialize(Local.Items, Opts);
        string? fileText = null;
        if (s.IsLinked)
        {
            try { fileText = Sources.ReadText(s.FilePath!); } catch { }
        }
        undo.Add(new Snap(label, local, s.IsLinked ? s.FilePath : null, fileText, Pinned.ToArray(), TodayMarks.ToArray()));
        if (undo.Count > 50) undo.RemoveAt(0);
    }

    void DropSnapshot()
    {
        if (undo.Count > 0) undo.RemoveAt(undo.Count - 1);
    }

    public bool CanUndo => undo.Count > 0;

    /// <summary>Reverts the last change. Returns its label, or null if there is nothing to undo.</summary>
    public string? Undo()
    {
        if (undo.Count == 0) return null;
        var sn = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        if (sn.LocalJson != null)
        {
            Local.Items = JsonSerializer.Deserialize<List<TodoItem>>(sn.LocalJson, Opts) ?? new();
            Local.Wire();
        }
        if (sn.FilePath != null && sn.FileText != null &&
            Linked.FirstOrDefault(x => string.Equals(x.FilePath, sn.FilePath, StringComparison.OrdinalIgnoreCase)) is { } sec)
        {
            if (Sources.Edit(sec, _ => sn.FileText) is { } err) LastError = err;
        }
        Pinned.Clear();
        foreach (var p in sn.Pinned) Pinned.Add(p);
        TodayMarks.Clear();
        foreach (var kv in sn.Today) TodayMarks[kv.Key] = kv.Value;
        Touch();
        return sn.Label;
    }

    // ---------- edits (local list and linked files) ----------

    /// <summary>Mark a todo (and its whole branch) OK / NOT OK. Linked files are written back.</summary>
    public string? SetComplete(TodoItem item, bool done)
    {
        Snapshot(done ? "OK" : "NOT OK", item.Section);
        var nodes = item.SelfAndDescendants().ToList();
        foreach (var n in nodes) n.Done = done;
        for (var p = item.Parent; p != null; p = p.Parent)
        {
            p.Done = p.Complete();
            nodes.Add(p);
        }
        string? err = item.Section.IsLinked ? Sources.WriteDone(item.Section, nodes) : null;
        Touch();
        return err;
    }

    public (TodoItem? item, string? error) Add(Section s, TodoItem? parent, int index, string text, string? due)
    {
        text = text.Trim();
        Snapshot("ADD", s);
        Collapsed.Remove("S:" + s.Id);
        if (parent != null) Collapsed.Remove(parent.Id);

        if (s.IsLinked)
        {
            string? parentId = parent?.Id;
            var err = Sources.Add(s, parent, index, text, due);
            if (err != null)
            {
                DropSnapshot();
                Touch();
                return (null, err);
            }
            var list = parentId == null ? s.Items : s.All().FirstOrDefault(i => i.Id == parentId)?.Children;
            var found = list is { Count: > 0 } ? list[Math.Clamp(index, 0, list.Count - 1)] : null;
            Touch();
            return (found, null);
        }

        var it = new TodoItem { Text = text, Due = due, Parent = parent, Section = s };
        var target = parent?.Children ?? s.Items;
        target.Insert(Math.Clamp(index, 0, target.Count), it);
        for (var p = parent; p != null; p = p.Parent) p.Done = p.Complete();
        Touch();
        return (it, null);
    }

    public string? Rename(TodoItem it, string text)
    {
        Snapshot("EDIT", it.Section);
        string? err = null;
        if (it.Section.IsLinked) err = Sources.Rename(it.Section, it, text);
        else it.Text = text.Trim();
        if (err != null) DropSnapshot();
        Touch();
        return err;
    }

    public string? SetDue(TodoItem it, string? due)
    {
        Snapshot("DUE DATE", it.Section);
        string? err = null;
        if (it.Section.IsLinked) err = Sources.SetDue(it.Section, it, due);
        else it.Due = due;
        if (err != null) DropSnapshot();
        Touch();
        return err;
    }

    public string? Delete(TodoItem it)
    {
        Snapshot("DELETE", it.Section);
        var ids = it.SelfAndDescendants().Select(d => d.Id).ToList();
        string? err = null;
        if (it.Section.IsLinked) err = Sources.Delete(it.Section, it);
        else
        {
            it.Siblings().Remove(it);
            for (var p = it.Parent; p != null; p = p.Parent) p.Done = p.Complete();
        }
        if (err != null)
        {
            DropSnapshot();
        }
        else
        {
            foreach (var id in ids)
            {
                Pinned.Remove(id);
                Collapsed.Remove(id);
                TodayMarks.Remove(id);
            }
        }
        Touch();
        return err;
    }

    /// <summary>Reorder within siblings (local list only).</summary>
    public bool Move(TodoItem it, int delta)
    {
        var l = it.Siblings();
        int i = l.IndexOf(it), j = i + delta;
        if (i < 0 || j < 0 || j >= l.Count) return false;
        Snapshot("MOVE", it.Section);
        l.RemoveAt(i);
        l.Insert(j, it);
        Touch();
        return true;
    }

    /// <summary>Remove completed top-level local todos.</summary>
    public int Purge()
    {
        var gone = Local.Items.Where(i => i.Complete()).ToList();
        if (gone.Count == 0) return 0;
        Snapshot("PURGE", Local);
        foreach (var it in gone)
        {
            Local.Items.Remove(it);
            foreach (var d in it.SelfAndDescendants()) { Pinned.Remove(d.Id); Collapsed.Remove(d.Id); TodayMarks.Remove(d.Id); }
        }
        Touch();
        return gone.Count;
    }

    public void TogglePin(string key)
    {
        Snapshot("PIN", Local);
        if (!Pinned.Remove(key)) Pinned.Add(key);
        Touch();
    }

    public void ToggleToday(string key)
    {
        Snapshot("TODAY MARK", Local);
        if (IsMarkedToday(key)) TodayMarks.Remove(key);
        else TodayMarks[key] = Dates.TodayKey;
        Touch();
    }

    public void ToggleCollapse(string key)
    {
        if (!Collapsed.Remove(key)) Collapsed.Add(key);
        Touch();
    }

    // ---------- linked files ----------

    public string? Link(string path)
    {
        try { path = Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return "BAD PATH: " + path; }
        if (!File.Exists(path)) return "FILE NOT FOUND: " + path;
        if (!Sources.Supported(path)) return "UNSUPPORTED FILE TYPE (USE .md OR .json)";
        if (Linked.Any(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            return "ALREADY LINKED: " + Path.GetFileName(path);

        var sec = new Section { FilePath = path, Id = Sources.LinkId(path), Title = Sources.DefaultTitle(path) };
        Linked.Add(sec);
        if (!Sources.Load(sec)) MarkDirty(sec);
        Watch(sec);
        Touch();
        return null;
    }

    public void Unlink(Section s)
    {
        if (watchers.Remove(s, out var w)) w.Dispose();
        Linked.Remove(s);
        dirty.Remove(s);
        Pinned.Remove("S:" + s.Id);
        Touch();
    }

    public void ReloadAll()
    {
        foreach (var s in Linked) MarkDirty(s);
    }

    /// <summary>Start file watchers. Events are delivered on the UI thread via <paramref name="ui"/>.</summary>
    public void StartWatching(ISynchronizeInvoke ui)
    {
        sync = ui;
        foreach (var s in Linked) Watch(s);
        if (dirty.Count > 0) reload.Start();
    }

    void Watch(Section s)
    {
        if (sync == null || watchers.ContainsKey(s)) return;
        try
        {
            string? dir = Path.GetDirectoryName(s.FilePath);
            if (dir == null || !Directory.Exists(dir)) return;
            var w = new FileSystemWatcher(dir, Path.GetFileName(s.FilePath!))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                SynchronizingObject = sync,
                InternalBufferSize = 4096,
            };
            FileSystemEventHandler h = (_, _) => MarkDirty(s);
            w.Changed += h;
            w.Created += h;
            w.Deleted += h;
            w.Renamed += (_, _) => MarkDirty(s);
            w.EnableRaisingEvents = true;
            watchers[s] = w;
        }
        catch { /* folder not watchable; :reload still works */ }
    }

    void MarkDirty(Section s)
    {
        if (!Linked.Contains(s)) return;
        dirty.Add(s);
        busyTicks = 0;
        reload.Stop();
        reload.Start(); // debounce editors that write in several steps
    }

    void ReloadDirty()
    {
        reload.Stop();
        foreach (var s in dirty.ToList())
            if (Sources.Load(s)) dirty.Remove(s);
        if (dirty.Count > 0 && ++busyTicks < 40) reload.Start(); // file was locked: retry
        Changed?.Invoke();
    }
}
