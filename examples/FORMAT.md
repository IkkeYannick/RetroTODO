# Getting your app's todos into Retro Todo

Your app doesn't talk to Retro Todo directly. It keeps its todos in **one file**,
and Retro Todo watches that file. There are two steps:

1. Write the todos to a `.md` or `.json` file in one of the formats below.
2. Link that file **once**: press `L` in Retro Todo, drag the file onto its window, or run
   `"%LOCALAPPDATA%\Programs\RetroTodo\RetroTodo.exe" --link "C:\path\to\todo.json"`

After that, whenever your app rewrites the file, Retro Todo updates within about 0.25 seconds.
Changes you make in Retro Todo (OK / NOT OK, add, edit, delete, due dates) are written back into the
same file, so your app can read them too.

---

## Option A: Markdown (easiest if a human edits it)

```markdown
# My App
- [ ] Todo @due(2026-11-20 18:00)
  - [ ] Subtodo @due(2026-11-19)
    - [ ] Sub-subtodo
- [x] A finished todo
```

Rules:
- A todo line is `- [ ] text` (open) or `- [x] text` (done). `*` or `+` also work instead of `-`.
- Indent **2 spaces** (or a tab) per level. Three levels at most; anything deeper is shown at level 3.
- Optional due date at the end of the line: `@due(2026-11-20)` or `@due(2026-11-20 18:00)`.
- The first `# Heading` is the list's name. Without one, the folder + file name is used.
- All other lines (notes, headings, code) are ignored, so this can live inside an existing README.

## Option B: JSON (easiest if code writes it)

```json
{
  "project": "My App",
  "todos": [
    {
      "text": "Todo",
      "done": false,
      "due": "2026-11-20T18:00",
      "children": [
        {
          "text": "Subtodo",
          "done": false,
          "children": [
            { "text": "Sub-subtodo", "done": true }
          ]
        }
      ]
    },
    { "text": "A finished todo", "done": true }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `project` | no | list name shown in Retro Todo |
| `todos` | yes | array of todo objects (a bare top-level array `[...]` also works) |
| `text` | yes | the todo text |
| `done` | no | `true` / `false` (default `false`) |
| `due` | no | `"2026-11-20"` (whole day) or `"2026-11-20T18:00"` (with a time, gets reminders) |
| `children` | no | array of subtodos, same shape (max 3 levels total) |

Leniencies: `title`/`name` work for `text`; `completed`/`checked` or `"status": "done"` work for `done`;
`dueDate`/`due_date`/`deadline` work for `due` (full ISO like `2026-11-20T18:00:00Z` is fine);
`subtasks`/`items`/`tasks` work for `children`. A plain string like `"Buy milk"` counts as an open todo.
Extra fields of your own (ids, dates, priority…) are allowed and are kept when Retro Todo writes back.

**Tip:** when your app regenerates the file, keep each todo's existing `done` value. Read the file
first and merge, or ticks you made in Retro Todo get overwritten.

---

## Copy-paste exporters

### C#
```csharp
using System.Text.Json;

var data = new {
    project = "My App",
    todos = new[] {
        new { text = "Login screen", done = false, children = new[] {
            new { text = "Layout", done = true },
            new { text = "Validation", done = false } } },
    }
};
File.WriteAllText("todo.json", JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
```

### Python
```python
import json

todos = {
    "project": "My App",
    "todos": [
        {"text": "Login screen", "done": False, "children": [
            {"text": "Layout", "done": True},
            {"text": "Validation", "done": False},
        ]},
    ],
}
with open("todo.json", "w", encoding="utf-8") as f:
    json.dump(todos, f, indent=2)
```

### JavaScript / Node
```js
const fs = require("fs");

fs.writeFileSync("todo.json", JSON.stringify({
  project: "My App",
  todos: [
    { text: "Login screen", done: false, children: [
      { text: "Layout", done: true },
      { text: "Validation", done: false },
    ]},
  ],
}, null, 2));
```

### Register the file from your app (optional, run once)
```powershell
& "$env:LOCALAPPDATA\Programs\RetroTodo\RetroTodo.exe" --link "C:\dev\MyApp\todo.json"
```
Linking the same file twice is harmless. Retro Todo answers "ALREADY LINKED" and ignores it.

See [TODO.md](TODO.md) and [todo.json](todo.json) in this folder for complete working examples.
