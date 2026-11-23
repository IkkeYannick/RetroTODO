# RETRO-TODO

A green-phosphor terminal todo app for Windows. It starts with your PC and keeps your todos in sight.
It's one small `.exe` built on WinForms, with no browser engine, and it uses no CPU while idle.

## Install

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This builds the app and installs it to `%LOCALAPPDATA%\Programs\RetroTodo`. It also sets it to start with
Windows (it opens as the overlay), adds **Retro Todo** shortcuts (with the green `>_` icon) to the Start menu
and the Desktop, and starts it. If it ever isn't open, click that shortcut. Clicking it while the app runs brings it
forward: side tab → overlay → full view.
Use `.\uninstall.ps1` to remove it again. Your todos are kept unless you pass `-RemoveData`.

Windows **Smart App Control** blocks unsigned `.exe` files, so the app is installed as `RetroTodo.dll` and started
by Microsoft's signed `dotnet.exe`, through `conhost.exe --headless` so no console window appears.
In Task Manager it shows up as *.NET Host*.

**Safety:** if `todos.json` is locked at boot, the app waits for it and never saves over a file it couldn't read.
A last good copy is kept as `todos.json.backup`. Linked files that aren't reachable yet (for example OneDrive
still starting up) show their last copy from `%APPDATA%\RetroTodo\cache` and are retried every 30 s.

Requires the .NET 10 Desktop Runtime (already installed on this PC).

## The three views

```
  ┌──────────────┐   F2 / [≡]   ┌──────────────┐  [»] / F3   ┌───┐
  │ 1 FULL VIEW  │ ───────────► │ 2 OVERLAY    │ ──────────► │ 3 │ SIDE TAB
  │ edit, add,   │ ◄─────────── │ pinned todos,│ ◄────────── │   │ (screen edge)
  │ remove       │    [≡]       │ always on top│  drag/click └───┘
  └──────────────┘              └──────────────┘
          └────────────── F3 / window X ─────────────────────► 3
```

| From → To | How |
|---|---|
| 1 → 2 | `F2`, or `:overlay` |
| 1 → 3 | `F3`, `:hide`, or the window's **X** button |
| 2 → 1 | `[≡]` button on the overlay |
| 2 → 3 | `[»]` button on the overlay |
| 3 → 2 | drag the tab out of the screen edge (it stays where you drop it), or click it |
| 3 → 1 | not allowed: go through the overlay |

The overlay never takes keyboard focus away from the app you're working in. Drag it by its top two rows.
It shows **☼ TODAY** (due today, overdue, or marked with `T`) first, then your pinned todos.
The tray icon also offers these moves, plus **Quick add**, **Today's summary** and **Quit**.

## Keys (full view)

| Key | Action |
|---|---|
| `↑ ↓ PgUp PgDn Home End` | move |
| `Space` | mark **OK / NOT OK** (applies to the whole branch) |
| `A` | add todo below the cursor (keeps asking; Enter on an empty line stops). End with `@fri 18:00` to set a date |
| `S` | add subtask. Max 3 levels: todo › subtodo › sub-subtodo |
| `E` / `Enter` | edit |
| `D` | set or clear the due date |
| `T` | put on today's list (clears at midnight) |
| `Del` | delete (asks Y/N), or unlink a linked list |
| `P` | pin / unpin to the overlay (works on a todo or a whole list) |
| `/` | search (filters while you type) |
| `F` | filter: all → open → today. `Esc` clears search and filter |
| `Ctrl+Z` | undo (up to 50 steps, also in linked files) |
| `→ ←` | open / close a branch |
| `Alt+↑ / Alt+↓` | reorder |
| `L` | live-link a TODO file (or drag a file onto the window) |
| `F11` | fullscreen · `F2` overlay · `F3` slide away · `?` help |
| `:` | commands: `add`, `due`, `find`, `filter`, `undo`, `remind`, `morning`, `digest`, `hotkey`, `link`, `unlink`, `reload`, `purge`, `crt`, `autostart`, `quit` |

Progress: a todo with subtasks is the average of its subtasks, so splitting work up shows partial progress.
`[~]` means partly done.

## Due dates & reminders

Type dates the way you'd say them: `today 18:00`, `tmr`, `fri`, `20/11`, `20/11/2026 9:30`, `6pm`, `+2h`, `+3d`.
Overdue todos turn amber. You get Windows notifications:
- **15 minutes before** a timed todo is due (`:remind 30` to change, `:remind off` to disable)
- **at the due time**
- a **morning summary** of today's and overdue todos (`:morning 08:30`, `:morning off`). If the PC starts
  later than that, it shows shortly after startup. `:digest` shows it now.

Click a notification to open the app.

## Quick add from anywhere

Press **Ctrl+Alt+T** in any app, type `Buy milk @tomorrow 9am`, and press Enter. It goes into LOCAL.
You asked for Win+Shift+T, but Windows already uses that combination, so the app falls back to Ctrl+Alt+T.
Change it with `:hotkey Ctrl+Shift+Space` (or any free combination).

## Importing todos from your other apps (live link)

Link a file once and it stays in sync:
- Changes to the file show up in the app straight away.
- Everything you do in the app is written back into the file: OK / NOT OK, add, edit, delete, and due dates.
- Reordering linked todos is done in the file itself.

Link a file in any of these ways:
- press `L` in the full view
- drag the file onto the window
- run `RetroTodo.exe --link "C:\path\to\TODO.md"`. Your own apps or build scripts can do this; if
  Retro Todo is already running, the file gets added to the running copy.

### Markdown (`.md`)

```markdown
# My App            <- first heading becomes the list title (optional)
- [ ] Build login screen @due(2026-11-20 18:00)
  - [x] Layout
  - [ ] Validation
    - [ ] Email format
- [x] Set up project
```

Other lines are ignored, so this works on an existing README or TODO.md. `*` and `+` bullets work too.

### JSON (`.json`)

```json
{
  "project": "My App",
  "todos": [
    { "text": "Build login", "done": false, "due": "2026-11-20T18:00", "children": [
        { "text": "Layout", "done": true }
    ]},
    "plain strings work too"
  ]
}
```

It's forgiving about key names: `text`/`title`/`name`, `done`/`completed`/`checked` or `"status": "done"`,
`due`/`dueDate`/`deadline` (`2026-11-20`, `2026-11-20T18:00`, or full ISO),
and `children`/`subtasks`/`items`/`tasks`. It also accepts a bare top-level array, comments, and trailing commas.
Unknown keys are kept when the app writes back.

Examples are in [examples/](examples/). [examples/FORMAT.md](examples/FORMAT.md) has the full format guide
and copy-paste exporters for C#, Python and JavaScript.

## Resource use

- Nothing animates: no CPU use while idle. The prompt cursor blinks only while the full view has focus.
  Reminders wake the app only when something is due (and every 10 minutes to notice the day changing).
- Closing the full view frees its window and screen buffers. In overlay/side mode the app hands unused
  memory back to Windows: measured at about 1.3 MB working set and 14 MB private memory.
- `:crt off` turns off the glow, scanlines, and vignette if you want plain rendering.

## Files

- `%APPDATA%\RetroTodo\todos.json`: your local todos, pins, links, and window positions
- `src/`: the app (C# / WinForms, .NET 10)
