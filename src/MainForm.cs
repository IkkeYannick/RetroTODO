namespace RetroTodo;

/// <summary>View 1: the full terminal-style editor. Windowed or fullscreen (F11).</summary>
sealed class MainForm : Form
{
    sealed record Row(Section Sec, TodoItem? Item)
    {
        public string Key => Item?.Id ?? "S:" + Sec.Id;
    }

    sealed class Input
    {
        public string Prompt = "> ";
        public string Text = "";
        public int Caret;
        public Action<string>? Submit;
        public Action<string>? Changed; // live updates (search)
        public Action? Cancel;
        public Action? Yes;             // set => this is a [Y/N] question

        public void Insert(string t)
        {
            t = t.Replace("\r", "").Replace('\n', ' ').Replace('\t', ' ');
            Text = Text.Insert(Caret, t);
            Caret += t.Length;
        }
    }

    static readonly string[] FilterNames = { "ALL", "OPEN", "TODAY" };

    static readonly string[] HelpText =
    {
        "KEYS",
        "  ↑ ↓ PGUP PGDN HOME END   move            SPACE   mark OK / NOT OK (whole branch)",
        "  A  add todo below        S  add subtask  E/ENTER edit text      DEL  delete",
        "  D  set due date          T  mark today   P  pin to overlay      ^Z   undo",
        "  /  search                F  filter: all → open → today          ESC  clear search/filter",
        "  → ←  open/close branch   ALT+↑↓ reorder  L  link a file (or drop it on the window)",
        "  F2 overlay   F3 slide away   F11 fullscreen   QUICK ADD from anywhere: see :hotkey",
        "",
        "DATES  (D, or type '@' at the end of a new todo:  Call mom @fri 18:00)",
        "  today  tmr  fri  20/11  20/11/2026  2026-11-20  18:00  6pm  +2h  +30m  +3d  +1w",
        "",
        "COMMANDS  (type : first)",
        "  :add <text>  :due <date>  :find <text>  :filter all|open|today  :undo  :purge",
        "  :remind <min>|off  :morning <HH:mm>|off  :digest  :hotkey <keys>",
        "  :link <path>  :unlink  :reload  :crt on|off  :autostart on|off  :overlay  :hide  :quit",
        "",
        "LINKED FILES   everything works on them too: edits are written into the file.",
        "  Markdown  - [ ] task @due(2026-11-20 18:00)   indent 2 spaces per level",
        "  JSON      {\"text\":\"..\",\"done\":false,\"due\":\"2026-11-20T18:00\",\"children\":[]}",
        "",
        "PRESS ANY KEY",
    };

    readonly AppController app;
    Store Store => app.Store;
    readonly Crt crt;
    readonly List<Row> rows = new();
    int sel, top, filter;
    string search = "";
    string? selKey;
    Input? input;
    string message = "";
    bool messageError, help, fullscreen, layerDirty = true, caretOn = true;
    Rectangle restore;
    FormWindowState restoreState;
    Rectangle screenRect, textRect;
    int cols = 80, lines = 24;
    readonly System.Windows.Forms.Timer blink = new() { Interval = 530 };
    public bool AllowClose;

    public MainForm(AppController app)
    {
        this.app = app;
        Text = "RETRO-TODO";
        Icon = app.AppIcon;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bezel;
        AllowDrop = true;
        KeyPreview = true;

        float k = DeviceDpi / 96f;
        crt = new Crt(16f * k);
        MinimumSize = new Size((int)(560 * k), (int)(360 * k));
        StartPosition = FormStartPosition.Manual;

        var w = Store.Settings.Window;
        var b = w is { Length: 4 } ? new Rectangle(w[0], w[1], w[2], w[3]) : Rectangle.Empty;
        if (b.Width < MinimumSize.Width || !Screen.AllScreens.Any(x => x.WorkingArea.IntersectsWith(b)))
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            var sz = new Size(Math.Min(wa.Width - 80, (int)(1000 * k)), Math.Min(wa.Height - 80, (int)(680 * k)));
            b = new Rectangle(wa.X + (wa.Width - sz.Width) / 2, wa.Y + (wa.Height - sz.Height) / 2, sz.Width, sz.Height);
        }
        Bounds = b;
        if (Store.Settings.Maximized) WindowState = FormWindowState.Maximized;

        blink.Tick += (_, _) => { caretOn = !caretOn; Invalidate(CaretRect()); };
        Rebuild();
        if (Store.LastError != null) Error(Store.LastError);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitle(Handle);
    }

    // ---------- external hooks ----------

    public void RefreshContent()
    {
        Rebuild();
        Redraw();
    }

    public void Notice(string msg) => Info(msg);

    public void SaveWindow()
    {
        Rectangle r = fullscreen ? restore : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        Store.Settings.Window = new[] { r.X, r.Y, r.Width, r.Height };
        Store.Settings.Maximized = fullscreen ? restoreState == FormWindowState.Maximized : WindowState == FormWindowState.Maximized;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindow();
        Store.Save();
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            // The X button slides the app away to the side tab (view 1 -> 3).
            e.Cancel = true;
            BeginInvoke(new Action(app.GoSide));
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            blink.Dispose();
            crt.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- rows, filtering & selection ----------

    Row? Cur => sel >= 0 && sel < rows.Count ? rows[sel] : null;

    bool Filtering => search.Length > 0 || filter != 0;

    bool Keep(TodoItem it, DateTime now)
    {
        if (search.Length > 0 && !it.Text.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter == 1 && it.Complete()) return false;
        if (filter == 2 && !Store.IsToday(it, now)) return false;
        return true;
    }

    /// <summary>Shown when it matches, or when something below it matches (so you see where it lives).</summary>
    bool IsShown(TodoItem it, DateTime now) => Keep(it, now) || it.Children.Any(c => IsShown(c, now));

    void Rebuild()
    {
        string? key = selKey;
        var now = DateTime.Now;
        rows.Clear();
        foreach (var s in Store.Sections)
        {
            if (Filtering && !s.Items.Any(i => IsShown(i, now))) continue;
            rows.Add(new Row(s, null));
            if (!Filtering && Store.Collapsed.Contains("S:" + s.Id)) continue;
            foreach (var it in s.Items) AddRows(s, it, now);
        }
        int idx = key == null ? -1 : rows.FindIndex(r => r.Key == key);
        if (idx < 0) idx = Math.Min(sel, rows.Count - 1);
        Select(Math.Max(0, idx));
    }

    void AddRows(Section s, TodoItem it, DateTime now)
    {
        if (Filtering && !IsShown(it, now)) return;
        rows.Add(new Row(s, it));
        if (it.Children.Count > 0 && (Filtering || !Store.Collapsed.Contains(it.Id)))
            foreach (var c in it.Children) AddRows(s, c, now);
    }

    void Select(int i)
    {
        sel = Math.Clamp(i, 0, Math.Max(0, rows.Count - 1));
        if (rows.Count > 0) selKey = rows[sel].Key;
        EnsureVisible();
        Redraw();
    }

    int ListLines => Math.Max(1, lines - 5);

    void EnsureVisible()
    {
        int n = ListLines;
        if (sel < top) top = sel;
        if (sel >= top + n) top = sel - n + 1;
        top = Math.Clamp(top, 0, Math.Max(0, rows.Count - n));
    }

    // ---------- layout & painting ----------

    void DoLayout()
    {
        float k = DeviceDpi / 96f;
        int m = fullscreen ? 0 : (int)(10 * k);
        screenRect = Rectangle.Inflate(ClientRectangle, -m, -m);
        int pad = (int)(16 * k);
        textRect = Rectangle.Inflate(screenRect, -pad, -pad);
        cols = Math.Max(30, (int)(textRect.Width / crt.CW));
        lines = Math.Max(9, (int)(textRect.Height / crt.CH));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (crt == null) return;
        DoLayout();
        EnsureVisible();
        Redraw();
    }

    void Redraw()
    {
        layerDirty = true;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        DoLayout();
        crt.Effects = Store.Settings.Crt;
        if (layerDirty || crt.LayerSize != screenRect.Size)
        {
            var g = crt.BeginLayer(screenRect.Size);
            DrawScreen(g);
            crt.EndLayer(g);
            layerDirty = false;
        }
        float k = DeviceDpi / 96f;
        crt.Present(e.Graphics, ClientRectangle, screenRect, !fullscreen, fullscreen ? 0 : (int)(18 * k));
        DrawCaret(e.Graphics);
    }

    float OX => textRect.X - screenRect.X;
    float OY => textRect.Y - screenRect.Y;

    void Put(Graphics g, int col, int line, string s, Color c) => crt.Text(g, OX + col * crt.CW, OY + line * crt.CH, s, c);

    void Bar(Graphics g, int line) => g.FillRectangle(crt.Brush(Theme.Fg), OX - 3, OY + line * crt.CH, cols * crt.CW + 6, crt.CH);

    void DrawScreen(Graphics g)
    {
        var now = DateTime.Now;
        double total = Store.TotalProgress();
        string right = $"TOTAL {Theme.Pct(total).Trim()} {Theme.Bar(total, 12)}";
        string left = "RETRO-TODO v1.1";
        if (filter != 0) left += $"  [{FilterNames[filter]}]";
        if (search.Length > 0) left += $"  FIND: {search}";
        Put(g, 0, 0, Theme.Fit(left, Math.Max(0, cols - right.Length - 1)), Theme.Fg);
        Put(g, Math.Max(0, cols - right.Length), 0, right, Theme.Fg);

        int n = ListLines;
        string sepTop = new string('═', cols), sepBottom = sepTop;
        if (!help && top > 0) sepTop = sepTop[..(cols - 4)] + " ▲ ═";
        if (!help && top + n < rows.Count) sepBottom = sepBottom[..(cols - 4)] + " ▼ ═";
        Put(g, 0, 1, sepTop, Theme.Dim);
        Put(g, 0, lines - 3, sepBottom, Theme.Dim);

        if (help)
        {
            for (int i = 0; i < Math.Min(n, HelpText.Length); i++)
            {
                string h = HelpText[i];
                bool heading = h.Length > 0 && h[0] != ' ';
                Put(g, 0, 2 + i, Theme.Fit(h, cols), heading ? Theme.Fg : Theme.Dim);
            }
        }
        else if (rows.Count == 0)
        {
            Put(g, 2, 2, Filtering ? "NO MATCHES. PRESS ESC TO CLEAR THE SEARCH/FILTER." : "NOTHING HERE YET. PRESS A TO ADD A TODO.", Theme.Dim);
        }
        else
        {
            for (int i = 0; i < n && top + i < rows.Count; i++)
                DrawRow(g, 2 + i, rows[top + i], top + i == sel, now);
        }

        if (message.Length > 0)
            Put(g, 0, lines - 2, Theme.Fit((messageError ? "!! " : "") + message, cols), messageError ? Theme.Amber : Theme.Fg);
        else
            Put(g, 0, lines - 2, Theme.Fit("SPACE ok · A add · S sub · E edit · D due · T today · P pin · / find · F filter · ^Z undo · ? help", cols), Theme.Dim);

        var (p, t, _) = PromptParts();
        Put(g, 0, lines - 1, p + t, Theme.Fg);
    }

    void DrawRow(Graphics g, int line, Row r, bool selected, DateTime now)
    {
        string text;
        Color c = Theme.Fg;
        if (r.Item == null)
        {
            var s = r.Sec;
            string arrow = !Filtering && Store.Collapsed.Contains(r.Key) ? "►" : "▼";
            string pin = Store.Pinned.Contains(r.Key) ? "■ " : "";
            string left = $"{arrow} {pin}{s.Title.ToUpperInvariant()}{(s.IsLinked ? " [LINK]" : "")} ";
            if (s.Error != null) left += $"!! {s.Error} ";
            string right = s.Items.Count == 0 ? " EMPTY" : $" {Theme.Pct(s.Progress())} {Theme.Bar(s.Progress(), 10)}";
            left = Theme.Fit(left, Math.Max(0, cols - right.Length - 1));
            text = left + new string('═', Math.Max(1, cols - left.Length - right.Length)) + right;
            if (s.Error != null && !selected) c = Theme.Amber;
        }
        else
        {
            var it = r.Item;
            int indent = 2 + it.Depth() * 3;
            string exp = it.Children.Count == 0 ? " " : !Filtering && Store.Collapsed.Contains(it.Id) ? "►" : "▼";
            bool complete = it.Complete();
            double prog = it.Progress();
            string box = complete ? "[X]" : prog > 0 ? "[~]" : "[ ]";
            string marks = (Store.Pinned.Contains(it.Id) ? "■ " : "") + (Store.IsMarkedToday(it.Id) && !complete ? "☼ " : "");
            string due = Dates.TryParseStored(it.Due, out var d, out var ht) ? " " + Dates.Label(d, ht, now, complete) : "";
            string right = due + (it.Children.Count > 0 ? " " + Theme.Pct(prog) : "");
            string left = new string(' ', indent) + exp + " " + box + " " + marks;
            int room = Math.Max(0, cols - left.Length - right.Length);
            text = left + Theme.Fit(it.Text, room).PadRight(room) + right;
            if (complete) c = Theme.Dim;
            else if (Dates.IsOverdue(it, now)) c = Theme.Amber;
        }

        if (selected)
        {
            Bar(g, line);
            Put(g, 0, line, Theme.Fit(text, cols), Theme.Bg);
        }
        else Put(g, 0, line, Theme.Fit(text, cols), c);
    }

    (string prompt, string text, int caretCol) PromptParts()
    {
        if (input == null) return ("> ", "", 2);
        string p = input.Prompt;
        int room = Math.Max(1, cols - p.Length - 1);
        int start = Math.Max(0, input.Caret - room + 1);
        string t = input.Text.Length - start > room ? input.Text.Substring(start, room) : input.Text[start..];
        return (p, t, p.Length + input.Caret - start);
    }

    Rectangle CaretRect()
    {
        var (_, _, c) = PromptParts();
        var r = new RectangleF(textRect.X + c * crt.CW, textRect.Y + (lines - 1) * crt.CH, crt.CW, crt.CH);
        return Rectangle.Inflate(Rectangle.Ceiling(r), 1, 1);
    }

    void DrawCaret(Graphics g)
    {
        if (!caretOn || help) return;
        var (_, _, c) = PromptParts();
        float x = textRect.X + c * crt.CW, y = textRect.Y + (lines - 1) * crt.CH;
        g.FillRectangle(crt.Brush(Theme.Fg), x, y + 1, crt.CW, crt.CH - 2);
        if (input != null && input.Caret < input.Text.Length)
            crt.Text(g, x, y, input.Text[input.Caret].ToString(), Theme.Bg);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (!Focused) Focus(); // keys only arrive as normal key messages when the form has focus
        caretOn = true;
        blink.Start();
        Invalidate(CaretRect());
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        blink.Stop(); // no CPU use while you work in other apps
        caretOn = true;
        Invalidate(CaretRect());
    }

    // ---------- messages & prompts ----------

    void Info(string m) { message = m; messageError = false; Redraw(); }

    void Error(string m) { message = m; messageError = true; Redraw(); }

    void Ask(string prompt, string initial, Action<string> submit)
    {
        help = false;
        input = new Input { Prompt = prompt, Text = initial, Caret = initial.Length, Submit = submit };
        Redraw();
    }

    void Confirm(string prompt, Action yes)
    {
        help = false;
        input = new Input { Prompt = prompt, Yes = yes };
        Redraw();
    }

    void SubmitInput()
    {
        var i = input!;
        input = null;
        message = "";
        if (i.Yes == null) i.Submit?.Invoke(i.Text); // Enter on a [Y/N] question = no
        Redraw();
    }

    void CancelInput()
    {
        var i = input;
        input = null;
        message = "";
        i?.Cancel?.Invoke();
        Redraw();
    }

    void InputEdited()
    {
        input?.Changed?.Invoke(input.Text);
        Redraw();
    }

    // ---------- keyboard ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    bool HandleKey(Keys k)
    {
        Keys code = k & Keys.KeyCode;
        bool alt = (k & Keys.Alt) != 0, ctrl = (k & Keys.Control) != 0;

        if (code == Keys.F11) { ToggleFullscreen(); return true; }

        if (input != null)
        {
            switch (code)
            {
                case Keys.Enter: SubmitInput(); return true;
                case Keys.Escape: CancelInput(); return true;
                case Keys.Back:
                    if (input.Caret > 0) { input.Text = input.Text.Remove(input.Caret - 1, 1); input.Caret--; }
                    InputEdited(); return true;
                case Keys.Delete:
                    if (input.Caret < input.Text.Length) input.Text = input.Text.Remove(input.Caret, 1);
                    InputEdited(); return true;
                case Keys.Left: input.Caret = Math.Max(0, input.Caret - 1); Redraw(); return true;
                case Keys.Right: input.Caret = Math.Min(input.Text.Length, input.Caret + 1); Redraw(); return true;
                case Keys.Home: input.Caret = 0; Redraw(); return true;
                case Keys.End: input.Caret = input.Text.Length; Redraw(); return true;
                case Keys.V when ctrl:
                    if (input.Yes == null && Clipboard.ContainsText()) { input.Insert(Clipboard.GetText()); InputEdited(); }
                    return true;
                case Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Tab: return true;
            }
            return false; // printable characters arrive in OnKeyPress
        }

        if (help)
        {
            if (code is Keys.ShiftKey or Keys.ControlKey or Keys.Menu) return false;
            help = false;
            Redraw();
            return true;
        }

        switch (code)
        {
            case Keys.Z when ctrl: Undo(); return true;
            case Keys.Up when alt: MoveItem(-1); return true;
            case Keys.Down when alt: MoveItem(1); return true;
            case Keys.Up: Select(sel - 1); return true;
            case Keys.Down: Select(sel + 1); return true;
            case Keys.PageUp: Select(sel - ListLines); return true;
            case Keys.PageDown: Select(sel + ListLines); return true;
            case Keys.Home: Select(0); return true;
            case Keys.End: Select(rows.Count - 1); return true;
            case Keys.Right: Expand(); return true;
            case Keys.Left: Collapse(); return true;
            case Keys.Enter: Edit(); return true;
            case Keys.Delete: Delete(); return true;
            case Keys.F2: app.GoOverlay(); return true;
            case Keys.F3: app.GoSide(); return true;
            case Keys.Escape:
                if (fullscreen) ToggleFullscreen();
                else if (Filtering) { search = ""; filter = 0; Rebuild(); Info("SHOWING EVERYTHING."); }
                else { message = ""; Redraw(); }
                return true;
        }
        return false;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        e.Handled = true;
        char ch = e.KeyChar;

        if (input != null)
        {
            if (input.Yes != null)
            {
                var yes = input.Yes;
                input = null;
                message = "";
                if (ch is 'y' or 'Y' or 'j' or 'J') yes();
                Redraw();
            }
            else if (!char.IsControl(ch))
            {
                input.Insert(ch.ToString());
                InputEdited();
            }
            return;
        }

        if (help) { help = false; Redraw(); return; }
        if (char.IsControl(ch)) return;

        switch (char.ToLowerInvariant(ch))
        {
            case ' ': Toggle(); break;
            case 'a': Add(false); break;
            case 's': Add(true); break;
            case 'e': Edit(); break;
            case 'd': DuePrompt(); break;
            case 't': ToggleToday(); break;
            case 'p': Pin(); break;
            case 'l': BrowseLink(); break;
            case 'f': CycleFilter(); break;
            case '/': SearchPrompt(); break;
            case ':': Ask(":", "", RunCommand); break;
            case '?' or 'h': help = true; Redraw(); break;
        }
    }

    // ---------- actions ----------

    void Report(string? err, string ok)
    {
        if (err != null) Error(err);
        else Info(ok);
    }

    void Toggle()
    {
        var r = Cur;
        if (r == null) return;
        if (r.Item == null) { Store.ToggleCollapse(r.Key); return; }
        bool now = !r.Item.Complete();
        Report(Store.SetComplete(r.Item, now), now ? "MARKED OK." : "MARKED NOT OK.");
    }

    void Add(bool sub)
    {
        var r = Cur;
        var sec = r?.Sec ?? Store.Local;
        if (sub)
        {
            if (r?.Item == null) { Error("SELECT A TODO FIRST, THEN PRESS S."); return; }
            if (r.Item.Depth() >= TodoItem.MaxDepth) { Error("MAX DEPTH REACHED: TODO > SUBTODO > SUB-SUBTODO."); return; }
            AskAdd(sec, r.Item, r.Item.Children.Count, "NEW SUBTASK> ");
        }
        else if (r?.Item == null) AskAdd(sec, null, sec.Items.Count, "NEW TODO> ");
        else AskAdd(sec, r.Item.Parent, r.Item.Siblings().IndexOf(r.Item) + 1, "NEW TODO> ");
    }

    /// <summary>Keeps asking so you can type several in a row; Enter on an empty line stops.</summary>
    void AskAdd(Section sec, TodoItem? parent, int index, string label)
    {
        Ask(label, "", raw =>
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            Dates.SplitDue(raw, DateTime.Now, out var text, out var due, out var dateErr);
            if (text.Length == 0) { Error("TYPE THE TODO TEXT BEFORE THE @DATE."); return; }

            string? parentId = parent?.Id;
            var (it, err) = Store.Add(sec, parent, index, text, due);
            if (err != null) { Error(err); return; }
            if (it != null) selKey = it.Id;
            Rebuild();

            // linked files are reloaded after each write, so look the parent up again
            var freshParent = parentId == null ? null : sec.All().FirstOrDefault(x => x.Id == parentId);
            if (parentId != null && freshParent == null) return;
            AskAdd(sec, freshParent, index + 1, label);
            if (dateErr != null) Error(dateErr + " KEPT IT AS TEXT.");
            else Info("ADDED. TYPE THE NEXT ONE (END WITH @fri 18:00 FOR A DATE), OR ENTER ON AN EMPTY LINE TO STOP.");
        });
    }

    void Edit()
    {
        var r = Cur;
        if (r == null) return;
        if (r.Item == null) { Store.ToggleCollapse(r.Key); return; }
        var it = r.Item;
        Ask("EDIT> ", it.Text, t =>
        {
            if (!string.IsNullOrWhiteSpace(t) && t.Trim() != it.Text) Report(Store.Rename(it, t), "SAVED.");
        });
    }

    void DuePrompt(string? direct = null)
    {
        var r = Cur;
        if (r?.Item == null) { Error("SELECT A TODO FIRST, THEN PRESS D."); return; }
        var it = r.Item;

        void Apply(string t)
        {
            string? due = null;
            t = t.Trim();
            if (t.Length > 0 && t is not ("none" or "clear" or "-" or "off"))
            {
                if (!Dates.TryParseInput(t, DateTime.Now, out var d, out var ht))
                {
                    Error($"COULDN'T READ '{t}'. TRY: today 18:00 · fri · 20/11 · +2h");
                    return;
                }
                due = Dates.ToStored(d, ht);
            }
            string ok = due == null ? "DUE DATE REMOVED." : "DUE " + (Dates.TryParseStored(due, out var nd, out var nht) ? Dates.Label(nd, nht, DateTime.Now, false) : due) + ".";
            Report(Store.SetDue(it, due), ok);
        }

        if (direct != null) { Apply(direct); return; }
        string current = Dates.TryParseStored(it.Due, out var cd, out var cht) ? Dates.Human(cd, cht) : "";
        Ask("DUE> ", current, Apply);
        Info("today 18:00 · tmr · fri · 20/11 · 20/11/2026 9:30 · 6pm · +2h · +3d · EMPTY = NO DATE");
    }

    void ToggleToday()
    {
        var r = Cur;
        if (r?.Item == null) { Error("SELECT A TODO FIRST, THEN PRESS T."); return; }
        if (r.Item.Complete()) { Error("THAT ONE IS ALREADY DONE."); return; }
        Store.ToggleToday(r.Key);
        Info(Store.IsMarkedToday(r.Key) ? "☼ ON TODAY'S LIST (UNTIL MIDNIGHT)." : "REMOVED FROM TODAY'S LIST.");
    }

    void CycleFilter()
    {
        filter = (filter + 1) % FilterNames.Length;
        Rebuild();
        Info(filter switch
        {
            1 => "FILTER: OPEN TODOS ONLY. F AGAIN FOR TODAY, ESC FOR ALL.",
            2 => "FILTER: TODAY (DUE, OVERDUE, MARKED ☼). ESC FOR ALL.",
            _ => "FILTER OFF.",
        });
    }

    void SearchPrompt()
    {
        help = false;
        input = new Input
        {
            Prompt = "FIND> ",
            Text = search,
            Caret = search.Length,
            Changed = t => { search = t.Trim(); Rebuild(); },
            Submit = t => { search = t.Trim(); Rebuild(); if (search.Length > 0) Info($"{rows.Count(x => x.Item != null)} MATCHES. ESC CLEARS."); },
            Cancel = () => { search = ""; Rebuild(); },
        };
        Redraw();
    }

    void Undo()
    {
        var label = Store.Undo();
        if (label == null) Info("NOTHING TO UNDO.");
        else if (Store.LastError != null) Error("UNDO: " + Store.LastError);
        else Info($"UNDID: {label}.");
    }

    void Delete()
    {
        var r = Cur;
        if (r == null) return;
        if (r.Item == null)
        {
            if (!r.Sec.IsLinked) { Error("THE LOCAL LIST CAN'T BE DELETED."); return; }
            var s = r.Sec;
            Confirm($"UNLINK {Theme.Fit(s.Title, 30)}? (THE FILE IS NOT TOUCHED) [Y/N] ", () => { Store.Unlink(s); Info("UNLINKED."); });
            return;
        }
        var it = r.Item;
        int n = it.SelfAndDescendants().Count() - 1;
        string extra = n > 0 ? $" + {n} SUBTASK(S)" : "";
        string where = r.Sec.IsLinked ? " FROM THE FILE" : "";
        Confirm($"DELETE \"{Theme.Fit(it.Text, 30)}\"{extra}{where}? [Y/N] ", () => Report(Store.Delete(it), "DELETED. CTRL+Z TO UNDO."));
    }

    void Pin()
    {
        var r = Cur;
        if (r == null) return;
        Store.TogglePin(r.Key);
        Info(Store.Pinned.Contains(r.Key) ? "PINNED TO OVERLAY (F2 TO SEE IT)." : "UNPINNED.");
    }

    void Expand()
    {
        var r = Cur;
        if (r == null) return;
        bool has = r.Item == null || r.Item.Children.Count > 0;
        if (!has) return;
        if (!Filtering && Store.Collapsed.Contains(r.Key)) Store.ToggleCollapse(r.Key);
        else Select(sel + 1);
    }

    void Collapse()
    {
        var r = Cur;
        if (r == null) return;
        bool has = r.Item == null || r.Item.Children.Count > 0;
        if (!Filtering && has && !Store.Collapsed.Contains(r.Key)) { Store.ToggleCollapse(r.Key); return; }
        if (r.Item != null)
        {
            string parentKey = r.Item.Parent?.Id ?? "S:" + r.Sec.Id;
            int i = rows.FindIndex(x => x.Key == parentKey);
            if (i >= 0) Select(i);
        }
    }

    void MoveItem(int d)
    {
        var r = Cur;
        if (r?.Item == null) return;
        if (r.Sec.IsLinked) { Error("REORDER LINKED TODOS IN THE FILE ITSELF."); return; }
        Store.Move(r.Item, d);
    }

    void BrowseLink()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Live-link a TODO file",
            Filter = "TODO files (*.md;*.markdown;*.json;*.txt)|*.md;*.markdown;*.json;*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LinkPath(dlg.FileName);
    }

    void LinkPath(string path)
    {
        var err = Store.Link(path);
        if (err != null) { Error(err); return; }
        var s = Store.Linked[^1];
        selKey = "S:" + s.Id;
        Rebuild();
        Info($"LINKED {s.Title}: {s.All().Count()} TODOS. FILE CHANGES SHOW UP LIVE.");
    }

    void RunCommand(string line)
    {
        line = line.Trim();
        if (line.StartsWith(':')) line = line[1..];
        int sp = line.IndexOf(' ');
        string cmd = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
        string arg = sp < 0 ? "" : line[(sp + 1)..].Trim();

        switch (cmd)
        {
            case "":
                return;
            case "add":
                if (arg.Length == 0) { Add(false); break; }
                Dates.SplitDue(arg, DateTime.Now, out var text, out var due, out var dateErr);
                var (it, err) = Store.Add(Store.Local, null, Store.Local.Items.Count, text, due);
                if (it != null) { selKey = it.Id; Rebuild(); }
                Report(err ?? dateErr, "ADDED.");
                break;
            case "due":
                DuePrompt(arg.Length > 0 ? arg : null);
                break;
            case "find" or "search":
                search = arg;
                Rebuild();
                Info(search.Length > 0 ? $"{rows.Count(x => x.Item != null)} MATCHES. ESC CLEARS." : "SEARCH CLEARED.");
                break;
            case "filter":
                filter = Math.Max(0, Array.IndexOf(FilterNames, arg.ToUpperInvariant()));
                Rebuild();
                Info("FILTER: " + FilterNames[filter] + ".");
                break;
            case "today":
                filter = 2;
                Rebuild();
                Info("FILTER: TODAY. ESC FOR ALL.");
                break;
            case "undo":
                Undo();
                break;
            case "link":
                if (arg.Length == 0) BrowseLink(); else LinkPath(arg);
                break;
            case "unlink":
                if (Cur is { Sec.IsLinked: true } r) { Store.Unlink(r.Sec); Info("UNLINKED."); }
                else Error("MOVE THE CURSOR INTO A LINKED LIST FIRST.");
                break;
            case "reload":
                Store.ReloadAll();
                Info("RELOADING LINKED FILES.");
                break;
            case "purge":
                Info($"PURGED {Store.Purge()} COMPLETED TODO(S) FROM LOCAL. CTRL+Z TO UNDO.");
                break;
            case "remind":
                if (arg is "off" or "0") Store.Settings.RemindMinutes = 0;
                else if (int.TryParse(arg.TrimEnd('m', 'i', 'n'), out int min) && min is > 0 and <= 1440) Store.Settings.RemindMinutes = min;
                else if (arg.Length > 0) { Error("USE :remind 15  OR  :remind off"); break; }
                Store.Touch();
                Info(Store.Settings.RemindMinutes > 0
                    ? $"REMINDERS {Store.Settings.RemindMinutes} MIN BEFORE AND AT THE DUE TIME."
                    : "EARLY REMINDERS OFF (STILL NOTIFIES AT THE DUE TIME).");
                break;
            case "morning":
                if (arg == "off") Store.Settings.MorningTime = null;
                else if (Dates.TryParseClock(arg, out var mt)) Store.Settings.MorningTime = mt.ToString(@"hh\:mm");
                else if (arg.Length > 0) { Error("USE :morning 08:30  OR  :morning off"); break; }
                Store.Touch();
                Info(Store.Settings.MorningTime != null ? $"MORNING SUMMARY AT {Store.Settings.MorningTime}." : "MORNING SUMMARY OFF.");
                break;
            case "digest" or "summary":
                app.ShowDigest();
                Info("SENT TODAY'S SUMMARY AS A NOTIFICATION.");
                break;
            case "hotkey":
                Info(arg.Length == 0 ? app.HotkeyStatus + "  (CHANGE: :hotkey Ctrl+Alt+T)" : app.SetHotkey(arg));
                break;
            case "crt":
                Store.Settings.Crt = arg != "off";
                Store.Touch();
                Info("CRT EFFECTS " + (Store.Settings.Crt ? "ON." : "OFF."));
                break;
            case "autostart":
                if (arg is "on" or "off")
                {
                    try { AutoStart.Set(arg == "on"); }
                    catch (Exception ex) { Error("AUTOSTART FAILED: " + ex.Message); break; }
                }
                bool on = AutoStart.IsOn();
                Info("AUTOSTART " + (on ? "ON" : "OFF") + (on && !AutoStart.PointsHere() ? " (POINTS AT ANOTHER COPY OF THE APP)" : "") + ".");
                break;
            case "overlay":
                app.GoOverlay();
                break;
            case "hide" or "side":
                app.GoSide();
                break;
            case "full" or "fullscreen":
                ToggleFullscreen();
                break;
            case "help" or "?":
                help = true;
                Redraw();
                break;
            case "quit" or "exit" or "q":
                app.Quit();
                break;
            default:
                Error($"UNKNOWN COMMAND '{cmd}'. TYPE ? FOR HELP.");
                break;
        }
    }

    void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            restoreState = WindowState;
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            restore = Bounds;
            fullscreen = true;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            fullscreen = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = restore;
            WindowState = restoreState;
        }
        Redraw();
    }

    // ---------- mouse & drag-drop ----------

    (int line, int col) Cell(Point p) =>
        ((int)Math.Floor((p.Y - textRect.Y) / crt.CH), (int)Math.Floor((p.X - textRect.X) / crt.CW));

    int RowAt(int line)
    {
        int li = line - 2;
        if (help || li < 0 || li >= ListLines) return -1;
        int idx = top + li;
        return idx < rows.Count ? idx : -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var (line, col) = Cell(e.Location);
        int idx = RowAt(line);
        if (idx < 0) return;
        Select(idx);
        if (e.Button != MouseButtons.Left) return;
        var r = rows[idx];
        if (r.Item == null)
        {
            if (col <= 1 && !Filtering) Store.ToggleCollapse(r.Key);
            return;
        }
        int b = 2 + r.Item.Depth() * 3;
        if (col == b && r.Item.Children.Count > 0 && !Filtering) Store.ToggleCollapse(r.Key);
        else if (col >= b + 2 && col <= b + 4) Toggle();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var (line, col) = Cell(e.Location);
        int idx = RowAt(line);
        if (idx < 0 || rows[idx].Item is not { } it) return;
        if (col > 2 + it.Depth() * 3 + 4) Edit();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        top = Math.Clamp(top - Math.Sign(e.Delta) * 3, 0, Math.Max(0, rows.Count - ListLines));
        Redraw();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) LinkPath(f);
    }
}
