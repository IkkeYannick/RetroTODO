using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RetroTodo;

/// <summary>Reads and edits live-linked TODO files (Markdown checklists and JSON).</summary>
static class Sources
{
    public static bool IsJson(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public static bool Supported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".json" or ".txt";

    public static string LinkId(string path) => "L" + Hash(path.ToLowerInvariant());

    public static string DefaultTitle(string path)
    {
        string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "";
        return folder.Length > 0 ? $"{folder}/{Path.GetFileName(path)}" : Path.GetFileName(path);
    }

    /// <summary>
    /// Loads the section from disk. Returns false when the file can't be read right now
    /// (missing, locked, OneDrive not ready yet at boot...): the caller keeps retrying, and
    /// meanwhile the last good copy from the cache is shown so the list never looks empty.
    /// </summary>
    public static bool Load(Section s)
    {
        string text;
        try
        {
            text = Read(s.FilePath!, out _);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fallback(s, "FILE NOT FOUND");
        }
        catch (UnauthorizedAccessException)
        {
            return Fallback(s, "ACCESS DENIED");
        }
        catch (IOException)
        {
            return Fallback(s, "FILE BUSY");
        }

        if (!Parse(s, text))
        {
            s.Error = "PARSE ERROR"; // keep showing the last good version
            return true;
        }
        s.Error = null;
        SaveCache(s, text);
        return true;
    }

    static bool Parse(Section s, string text)
    {
        try
        {
            string? title;
            var items = IsJson(s.FilePath!) ? JsonTodo.Parse(text, out title) : MarkdownTodo.Parse(text, out title);
            s.Items = items;
            s.Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(s.FilePath!) : title.Trim();
            AssignIds(s);
            s.Wire();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool Fallback(Section s, string reason)
    {
        if (s.Items.Count == 0 && ReadCache(s) is { } cached) Parse(s, cached);
        s.Error = s.Items.Count > 0 ? reason + ", SHOWING LAST COPY" : reason + ", RETRYING";
        return false;
    }

    // ---------- last-good-copy cache (%APPDATA%\RetroTodo\cache) ----------

    static string CachePath(Section s) => Path.Combine(Paths.Cache, s.Id + (IsJson(s.FilePath!) ? ".json" : ".md"));

    static void SaveCache(Section s, string text)
    {
        string hash = Hash(text);
        if (s.CacheHash == hash) return;
        try
        {
            Directory.CreateDirectory(Paths.Cache);
            File.WriteAllText(CachePath(s), text, new UTF8Encoding(false));
            s.CacheHash = hash;
        }
        catch { }
    }

    static string? ReadCache(Section s)
    {
        try { return File.Exists(CachePath(s)) ? File.ReadAllText(CachePath(s)) : null; }
        catch { return null; }
    }

    public static string ReadText(string path) => Read(path, out _);

    /// <summary>Read the file, apply <paramref name="change"/>, write it back, reload. Returns an error or null.</summary>
    public static string? Edit(Section s, Func<string, string> change)
    {
        string path = s.FilePath!;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                string text = Read(path, out bool bom);
                string updated = change(text);
                if (updated != text) File.WriteAllText(path, updated, new UTF8Encoding(bom));
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                return "WRITE FAILED: " + ex.Message;
            }
        }
        Load(s);
        return null;
    }

    public static string? WriteDone(Section s, IEnumerable<TodoItem> nodes)
    {
        var list = nodes.ToList();
        return Edit(s, t => IsJson(s.FilePath!) ? JsonTodo.SetDone(t, list) : MarkdownTodo.SetDone(t, list));
    }

    public static string? Add(Section s, TodoItem? parent, int index, string text, string? due)
    {
        var siblings = parent?.Children ?? s.Items;
        return Edit(s, t => IsJson(s.FilePath!)
            ? JsonTodo.Add(t, parent, index, text, due)
            : MarkdownTodo.Add(t, parent, siblings, index, text, due));
    }

    public static string? Rename(Section s, TodoItem it, string text) =>
        Edit(s, t => IsJson(s.FilePath!) ? JsonTodo.Rename(t, it, text) : MarkdownTodo.Rename(t, it, text));

    public static string? Delete(Section s, TodoItem it) =>
        Edit(s, t => IsJson(s.FilePath!) ? JsonTodo.Delete(t, it) : MarkdownTodo.Delete(t, it));

    public static string? SetDue(Section s, TodoItem it, string? due) =>
        Edit(s, t => IsJson(s.FilePath!) ? JsonTodo.SetDue(t, it, due) : MarkdownTodo.SetDue(t, it, due));

    static string Read(string path, out bool bom)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return bom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Stable ids (so pins, folds and today-marks survive reloads): section + text path + occurrence.</summary>
    static void AssignIds(Section s)
    {
        var seen = new Dictionary<string, int>();
        void Walk(List<TodoItem> items, string prefix)
        {
            foreach (var it in items)
            {
                string chain = prefix + "/" + it.Text;
                int n = seen.TryGetValue(chain, out var c) ? c + 1 : 0;
                seen[chain] = n;
                it.Id = s.Id + "-" + Hash(chain + "#" + n);
                Walk(it.Children, chain);
            }
        }
        Walk(s.Items, "");
    }

    public static string Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h.ToString("x8");
    }
}

/// <summary>
/// Markdown checklists:  "- [ ] task" / "- [x] done"  (also * and +), optional "@due(2026-11-20 18:00)".
/// Indent to make subtasks. First "# Heading" becomes the list title.
/// </summary>
static class MarkdownTodo
{
    static readonly Regex Item = new(@"^(?<ind>[ \t]*)(?<bul>[-*+])[ \t]+\[(?<box>[ xX])\][ \t]?(?<text>.*?)\s*$");
    static readonly Regex Box = new(@"^([ \t]*[-*+][ \t]+\[)[ xX](\].*)$");
    static readonly Regex Heading = new(@"^#[ \t]+(.+?)\s*$");
    static readonly Regex DueTag = new(@"[ \t]*@due\(([^)]*)\)", RegexOptions.IgnoreCase);

    readonly record struct Line(string Indent, string Bullet, bool Done, string Text, string? Due);

    static Line? ParseLine(string line)
    {
        var m = Item.Match(line);
        if (!m.Success) return null;
        string text = m.Groups["text"].Value;
        string? due = null;
        var d = DueTag.Match(text);
        if (d.Success)
        {
            due = d.Groups[1].Value.Trim();
            text = DueTag.Replace(text, "").Trim();
        }
        return new Line(m.Groups["ind"].Value, m.Groups["bul"].Value, m.Groups["box"].Value != " ", text, due);
    }

    static string Compose(Line l, string eol) =>
        $"{l.Indent}{l.Bullet} [{(l.Done ? 'x' : ' ')}] {l.Text}{(string.IsNullOrWhiteSpace(l.Due) ? "" : $" @due({l.Due})")}{eol}";

    static string Eol(string line) => line.EndsWith('\r') ? "\r" : "";

    static string? FileDue(string? due) => due?.Replace('T', ' ');

    public static List<TodoItem> Parse(string text, out string? title)
    {
        title = null;
        var roots = new List<TodoItem>();
        var stack = new List<(int ind, TodoItem it)>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (title == null && Heading.Match(line) is { Success: true } h)
            {
                title = h.Groups[1].Value;
                continue;
            }
            if (ParseLine(line) is not { } l) continue;

            int ind = Indent(l.Indent);
            var it = new TodoItem { Text = l.Text, Done = l.Done, Due = l.Due, Line = i };
            while (stack.Count > 0 && stack[^1].ind >= ind) stack.RemoveAt(stack.Count - 1);
            while (stack.Count > TodoItem.MaxDepth) stack.RemoveAt(stack.Count - 1); // deeper levels are flattened
            if (stack.Count == 0) roots.Add(it);
            else stack[^1].it.Children.Add(it);
            stack.Add((ind, it));
        }
        return roots;
    }

    public static string SetDone(string text, List<TodoItem> nodes)
    {
        var lines = text.Split('\n');
        foreach (var it in nodes)
        {
            int i = Find(lines, it);
            if (i < 0) continue;
            var m = Box.Match(lines[i]);
            if (!m.Success) continue;
            lines[i] = m.Groups[1].Value + (it.Done ? "x" : " ") + m.Groups[2].Value;
        }
        return string.Join('\n', lines);
    }

    public static string Rename(string text, TodoItem it, string newText) => Rewrite(text, it, l => l with { Text = newText.Trim() });

    public static string SetDue(string text, TodoItem it, string? due) => Rewrite(text, it, l => l with { Due = FileDue(due) });

    static string Rewrite(string text, TodoItem it, Func<Line, Line> change)
    {
        var lines = text.Split('\n');
        int i = Find(lines, it);
        if (i < 0) throw new InvalidOperationException("todo not found in the file");
        lines[i] = Compose(change(ParseLine(lines[i])!.Value), Eol(lines[i]));
        return string.Join('\n', lines);
    }

    public static string Delete(string text, TodoItem it)
    {
        var lines = text.Split('\n').ToList();
        var idx = it.SelfAndDescendants().Select(d => Find(lines, d)).Where(i => i >= 0).Distinct().OrderByDescending(i => i).ToList();
        if (idx.Count == 0) throw new InvalidOperationException("todo not found in the file");
        foreach (int i in idx) lines.RemoveAt(i);
        return string.Join('\n', lines);
    }

    /// <summary>Insert a new checkbox line at sibling position <paramref name="index"/>.</summary>
    public static string Add(string text, TodoItem? parent, List<TodoItem> siblings, int index, string newText, string? due)
    {
        var lines = text.Split('\n').ToList();
        string eol = lines.Count > 1 && lines[0].EndsWith('\r') ? "\r" : "";
        string indent = "", bullet = "-";
        int at = -1;
        index = Math.Clamp(index, 0, siblings.Count);

        int prev = index > 0 ? Find(lines, siblings[index - 1]) : -1;
        int next = index < siblings.Count ? Find(lines, siblings[index]) : -1;
        int par = parent != null ? Find(lines, parent) : -1;

        if (prev >= 0)
        {
            var l = ParseLine(lines[prev])!.Value;
            indent = l.Indent;
            bullet = l.Bullet;
            at = siblings[index - 1].SelfAndDescendants().Select(d => Find(lines, d)).Max() + 1; // after its subtree
        }
        else if (next >= 0)
        {
            var l = ParseLine(lines[next])!.Value;
            indent = l.Indent;
            bullet = l.Bullet;
            at = next;
        }
        else if (par >= 0)
        {
            var l = ParseLine(lines[par])!.Value;
            indent = l.Indent + "  ";
            bullet = l.Bullet;
            at = par + 1;
        }
        else if (parent != null)
        {
            throw new InvalidOperationException("parent todo not found in the file");
        }
        else
        {
            // first todo in the file: append at the end
            if (lines.Count > 0 && lines[^1].Length == 0) at = lines.Count - 1;
            else
            {
                if (lines.Count > 0) lines[^1] += eol;
                at = lines.Count;
            }
        }

        string lineEol = at < lines.Count ? eol : "";
        lines.Insert(at, Compose(new Line(indent, bullet, false, newText.Trim(), FileDue(due)), lineEol));
        return string.Join('\n', lines);
    }

    static int Find(IList<string> lines, TodoItem it)
    {
        bool Matches(int i) => i >= 0 && i < lines.Count && ParseLine(lines[i]) is { } l && l.Text == it.Text;
        if (Matches(it.Line)) return it.Line;
        for (int i = 0; i < lines.Count; i++)
            if (Matches(i)) return i;
        return -1;
    }

    static int Indent(string ws)
    {
        int n = 0;
        foreach (char c in ws) n += c == '\t' ? 4 : 1;
        return n;
    }
}

/// <summary>
/// JSON: { "project": "My App", "todos": [ { "text": "...", "done": false, "due": "2026-11-20T18:00", "children": [ ... ] } ] }
/// Also accepts a bare array and common key names (title/name, completed, subtasks/items/tasks, status, dueDate/deadline).
/// </summary>
static class JsonTodo
{
    static readonly string[] TextKeys = { "text", "title", "name", "task", "content", "description" };
    static readonly string[] DoneKeys = { "done", "completed", "complete", "checked", "finished" };
    static readonly string[] StatusKeys = { "status", "state" };
    static readonly string[] DueKeys = { "due", "dueDate", "due_date", "deadline" };
    static readonly string[] ListKeys = { "children", "subtasks", "todos", "items", "tasks" };
    static readonly string[] TitleKeys = { "project", "name", "title", "app" };
    static readonly string[] DoneWords = { "done", "complete", "completed", "closed", "finished", "ok" };

    static readonly JsonDocumentOptions DocOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static List<TodoItem> Parse(string text, out string? title)
    {
        var root = JsonNode.Parse(text, null, DocOpts);
        var arr = RootList(root, out title);
        return ParseList(arr, Array.Empty<int>(), 0);
    }

    public static string SetDone(string text, List<TodoItem> nodes)
    {
        var (root, list) = Open(text);
        foreach (var it in nodes)
        {
            if (!Locate(list, it.JsonPath, out var parent, out int idx)) continue;
            var node = parent[idx];
            if (node is JsonObject o)
            {
                if (Find(o, DoneKeys) is { } dk) o[dk.Key] = it.Done;
                else if (Find(o, StatusKeys) is { } sk) o[sk.Key] = it.Done ? "done" : "todo";
                else o["done"] = it.Done;
            }
            else parent[idx] = new JsonObject { ["text"] = AsString(node) ?? "", ["done"] = it.Done };
        }
        return root.ToJsonString(WriteOpts);
    }

    public static string Add(string text, TodoItem? parentItem, int index, string newText, string? due)
    {
        var (root, list) = Open(text);
        JsonArray target = list;
        if (parentItem != null)
        {
            if (!Locate(list, parentItem.JsonPath, out var pa, out int pi)) throw new InvalidOperationException("parent todo not found in the file");
            if (pa[pi] is not JsonObject po)
            {
                po = new JsonObject { ["text"] = AsString(pa[pi]) ?? "", ["done"] = false };
                pa[pi] = po;
            }
            if (List(po) is { } existing) target = existing;
            else
            {
                target = new JsonArray();
                po["children"] = target;
            }
        }
        var obj = new JsonObject { ["text"] = newText.Trim(), ["done"] = false };
        if (due != null) obj["due"] = due;
        target.Insert(Math.Clamp(index, 0, target.Count), obj);
        return root.ToJsonString(WriteOpts);
    }

    public static string Rename(string text, TodoItem it, string newText)
    {
        var (root, list) = Open(text);
        if (!Locate(list, it.JsonPath, out var pa, out int i)) throw new InvalidOperationException("todo not found in the file");
        if (pa[i] is JsonObject o) o[Find(o, TextKeys)?.Key ?? "text"] = newText.Trim();
        else pa[i] = newText.Trim();
        return root.ToJsonString(WriteOpts);
    }

    public static string Delete(string text, TodoItem it)
    {
        var (root, list) = Open(text);
        if (!Locate(list, it.JsonPath, out var pa, out int i)) throw new InvalidOperationException("todo not found in the file");
        pa.RemoveAt(i);
        return root.ToJsonString(WriteOpts);
    }

    public static string SetDue(string text, TodoItem it, string? due)
    {
        var (root, list) = Open(text);
        if (!Locate(list, it.JsonPath, out var pa, out int i)) throw new InvalidOperationException("todo not found in the file");
        if (pa[i] is not JsonObject o)
        {
            o = new JsonObject { ["text"] = AsString(pa[i]) ?? "", ["done"] = false };
            pa[i] = o;
        }
        string? key = Find(o, DueKeys)?.Key;
        if (due == null) { if (key != null) o.Remove(key); }
        else o[key ?? "due"] = due;
        return root.ToJsonString(WriteOpts);
    }

    static (JsonNode root, JsonArray list) Open(string text)
    {
        var root = JsonNode.Parse(text, null, DocOpts) ?? new JsonObject();
        var list = RootList(root, out _);
        if (list == null)
        {
            if (root is not JsonObject o) throw new InvalidOperationException("unexpected JSON layout");
            list = new JsonArray();
            o["todos"] = list;
        }
        return (root, list);
    }

    static bool Locate(JsonArray root, int[]? path, out JsonArray parent, out int idx)
    {
        parent = root;
        idx = -1;
        if (path is not { Length: > 0 }) return false;
        JsonArray? a = root;
        for (int k = 0; k < path.Length; k++)
        {
            if (a == null || path[k] >= a.Count) return false;
            parent = a;
            idx = path[k];
            a = a[idx] is JsonObject ob ? List(ob) : null;
        }
        return true;
    }

    static JsonArray? RootList(JsonNode? root, out string? title)
    {
        title = null;
        if (root is JsonArray a) return a;
        if (root is JsonObject o)
        {
            title = AsString(Find(o, TitleKeys)?.Value);
            return List(o);
        }
        return null;
    }

    static List<TodoItem> ParseList(JsonArray? arr, int[] path, int depth)
    {
        var list = new List<TodoItem>();
        if (arr == null) return list;
        for (int i = 0; i < arr.Count; i++)
        {
            int[] p = [.. path, i];
            var n = arr[i];
            TodoItem it;
            List<TodoItem> kids = new();
            if (n is JsonObject o)
            {
                bool done = AsBool(Find(o, DoneKeys)?.Value)
                            || (AsString(Find(o, StatusKeys)?.Value) is { } st && DoneWords.Contains(st.Trim().ToLowerInvariant()));
                it = new TodoItem
                {
                    Text = AsString(Find(o, TextKeys)?.Value) ?? "(untitled)",
                    Done = done,
                    Due = AsString(Find(o, DueKeys)?.Value),
                };
                kids = ParseList(List(o), p, depth + 1);
            }
            else if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            {
                it = new TodoItem { Text = v.GetValue<string>() };
            }
            else continue;

            it.JsonPath = p;
            list.Add(it);
            if (depth >= TodoItem.MaxDepth) list.AddRange(kids); // flatten anything deeper than 3 levels
            else it.Children = kids;
        }
        return list;
    }

    static KeyValuePair<string, JsonNode?>? Find(JsonObject o, string[] keys)
    {
        foreach (var k in keys)
            foreach (var kv in o)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase))
                    return kv;
        return null;
    }

    static JsonArray? List(JsonObject o)
    {
        foreach (var k in ListKeys)
            foreach (var kv in o)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonArray a)
                    return a;
        return null;
    }

    static string? AsString(JsonNode? n) => n is JsonValue v
        ? v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.ToJsonString(),
            _ => null,
        }
        : null;

    static bool AsBool(JsonNode? n)
    {
        if (n is not JsonValue v) return false;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetValue<double>() != 0,
            JsonValueKind.String => v.GetValue<string>().Trim().ToLowerInvariant() is "true" or "yes" or "x" or "1" or "done",
            _ => false,
        };
    }
}
