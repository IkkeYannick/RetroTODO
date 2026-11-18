using System.Text.Json.Serialization;

namespace RetroTodo;

public sealed class TodoItem
{
    /// <summary>Depth 0 = todo, 1 = subtodo, 2 = sub-subtodo.</summary>
    public const int MaxDepth = 2;

    public string Id { get; set; } = NewId();
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    /// <summary>"yyyy-MM-dd" or "yyyy-MM-ddTHH:mm", or null.</summary>
    public string? Due { get; set; }
    public List<TodoItem> Children { get; set; } = new();

    // Runtime-only (fields are not serialized)
    [JsonIgnore] public TodoItem? Parent;
    [JsonIgnore] public Section Section = null!;
    [JsonIgnore] public int Line = -1;      // markdown source line
    [JsonIgnore] public int[]? JsonPath;    // json source position

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public int Depth() => Parent == null ? 0 : Parent.Depth() + 1;

    /// <summary>0..1. A leaf is 0 or 1; a parent is the average of its children.</summary>
    public double Progress() => Children.Count == 0 ? (Done ? 1 : 0) : Children.Average(c => c.Progress());

    public bool Complete() => Children.Count == 0 ? Done : Children.All(c => c.Complete());

    public List<TodoItem> Siblings() => Parent?.Children ?? Section.Items;

    public IEnumerable<TodoItem> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }

    /// <summary>First unfinished leaf, i.e. "what to do next".</summary>
    public TodoItem? NextOpenLeaf()
    {
        if (Children.Count == 0) return Done ? null : this;
        foreach (var c in Children)
            if (c.NextOpenLeaf() is { } l) return l;
        return null;
    }
}

public sealed class Section
{
    public string Id = "local";
    public string Title = "LOCAL";
    public string? FilePath;           // set for live-linked files
    public List<TodoItem> Items = new();
    public string? Error;

    public bool IsLinked => FilePath != null;

    public double Progress() => Items.Count == 0 ? 0 : Items.Average(i => i.Progress());

    public IEnumerable<TodoItem> All() => Items.SelectMany(i => i.SelfAndDescendants());

    public TodoItem? NextOpenLeaf() => Items.Select(i => i.NextOpenLeaf()).FirstOrDefault(x => x != null);

    public void Wire()
    {
        foreach (var i in Items) Wire(i, null);
    }

    void Wire(TodoItem it, TodoItem? parent)
    {
        it.Parent = parent;
        it.Section = this;
        it.Children ??= new();
        foreach (var c in it.Children) Wire(c, it);
    }
}

public sealed class Settings
{
    public int OverlayX { get; set; } = int.MinValue;
    public int OverlayY { get; set; } = int.MinValue;
    public bool Crt { get; set; } = true;
    public int[]? Window { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Minutes before a timed due date to remind (0 = off).</summary>
    public int RemindMinutes { get; set; } = 15;
    /// <summary>"HH:mm" for the morning summary, or null for off.</summary>
    public string? MorningTime { get; set; } = "09:00";
    public string? LastMorning { get; set; }
    public string Hotkey { get; set; } = "Win+Shift+T";
}

public sealed class StoreData
{
    public int Version { get; set; } = 1;
    public List<TodoItem> Todos { get; set; } = new();
    public List<string> Links { get; set; } = new();
    public List<string> Pinned { get; set; } = new();
    public List<string> Collapsed { get; set; } = new();
    /// <summary>Manual "today" marks: todo id -> date it was marked (only today's count).</summary>
    public Dictionary<string, string> Today { get; set; } = new();
    public Settings Settings { get; set; } = new();
}
