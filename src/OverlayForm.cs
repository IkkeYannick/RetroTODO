using System.Diagnostics;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RetroTodo;

/// <summary>
/// View 2 (small always-on-top overlay with pinned todos) and view 3 (a thin tab on the
/// right screen edge). Drag the tab out, or click it, to get the overlay back.
/// Never takes keyboard focus away from the app you are working in.
/// </summary>
sealed class OverlayForm : Form
{
    const int Cols = 38;
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    const double AnimMs = 190;

    readonly AppController app;
    Store Store => app.Store;
    readonly Crt crt;
    readonly float s;
    readonly int pad;
    readonly List<(string text, Color color)> body = new();
    bool tab;

    // mouse
    bool down, moved, pulling, onHeader;
    Point downAt, downLoc, grab;
    int hoverBtn = -1;

    // animation
    readonly System.Windows.Forms.Timer anim = new() { Interval = 15 };
    readonly Stopwatch sw = new();
    Rectangle aFrom, aTo;
    Action? aDone;

    public OverlayForm(AppController app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Retro Todo";
        BackColor = Theme.Bg;
        Opacity = 0.94;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        s = DeviceDpi / 96f;
        pad = (int)(8 * s);
        crt = new Crt(13.5f * s);
        anim.Tick += (_, _) => Step();
        Bounds = new Rectangle(-32000, -32000, 10, 10);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    // ---------- global hotkey (this window always exists, even when hidden) ----------

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    const int WM_HOTKEY = 0x0312, HotkeyId = 1;
    const uint MOD_NOREPEAT = 0x4000;

    public event Action? HotkeyPressed;
    public string? ActiveHotkey { get; private set; }

    /// <summary>Registers the hotkey; on failure the previous one stays active.</summary>
    public bool SetHotkey(string spec)
    {
        if (!Hotkeys.TryParse(spec, out var mods, out var vk)) return false;
        UnregisterHotKey(Handle, HotkeyId);
        if (RegisterHotKey(Handle, HotkeyId, mods | MOD_NOREPEAT, vk))
        {
            ActiveHotkey = spec;
            return true;
        }
        if (ActiveHotkey != null && Hotkeys.TryParse(ActiveHotkey, out var m2, out var v2))
            RegisterHotKey(Handle, HotkeyId, m2 | MOD_NOREPEAT, v2);
        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (ActiveHotkey != null && Hotkeys.TryParse(ActiveHotkey, out var mods, out var vk))
            RegisterHotKey(Handle, HotkeyId, mods | MOD_NOREPEAT, vk);
    }

    // ---------- public API used by AppController ----------

    public void ShowOverlay(bool fromSide)
    {
        BuildBody();
        var target = OverlayRect();
        bool wasTab = tab && Visible;
        tab = false;
        if (fromSide && wasTab)
        {
            var wa = Work(target.Location);
            Animate(new Rectangle(wa.Right, target.Y, target.Width, target.Height), target, null);
        }
        else
        {
            anim.Stop();
            Bounds = target;
        }
        if (!Visible) Show();
        Invalidate();
    }

    public void SlideAway(bool fromOverlay)
    {
        BuildBody();
        var t = TabRect();
        var wa = Work(t.Location);
        var tabStart = new Rectangle(wa.Right, t.Y, t.Width, t.Height);
        if (fromOverlay && Visible && !tab)
        {
            // slide the overlay off the edge, then slide the tab in
            Animate(Bounds, new Rectangle(wa.Right + 2, Top, Width, Height), () =>
            {
                tab = true;
                Invalidate();
                Animate(tabStart, t, null);
            });
        }
        else
        {
            tab = true;
            Animate(tabStart, t, null);
            if (!Visible) Show();
            Invalidate();
        }
    }

    public void HideAll()
    {
        anim.Stop();
        down = pulling = false;
        Hide();
    }

    public void RefreshContent()
    {
        BuildBody();
        if (!tab && Visible && !anim.Enabled && !pulling)
        {
            var sz = OverlaySize();
            if (Size != sz) Bounds = Clamp(new Rectangle(Location, sz));
        }
        Invalidate();
    }

    // ---------- geometry ----------

    Size OverlaySize() => new(
        (int)Math.Ceiling(Cols * crt.CW) + pad * 2,
        (int)Math.Ceiling((body.Count + 2) * crt.CH) + pad * 2);

    Size TabSize() => new((int)(26 * s), (int)(165 * s));

    static Rectangle Work(Point p) => Screen.FromPoint(p).WorkingArea;

    Point SavedPos(Size sz)
    {
        var st = Store.Settings;
        if (st.OverlayX == int.MinValue)
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            return new Point(wa.Right - sz.Width - (int)(16 * s), wa.Top + (int)(16 * s));
        }
        return new Point(st.OverlayX, st.OverlayY);
    }

    Rectangle OverlayRect()
    {
        var sz = OverlaySize();
        return Clamp(new Rectangle(SavedPos(sz), sz));
    }

    static Rectangle Clamp(Rectangle r)
    {
        var wa = Work(new Point(r.X + r.Width / 2, r.Y + Math.Min(r.Height / 2, 20)));
        int x = Math.Clamp(r.X, wa.Left, Math.Max(wa.Left, wa.Right - r.Width));
        int y = Math.Clamp(r.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - r.Height));
        return new Rectangle(x, y, r.Width, r.Height);
    }

    Rectangle TabRect()
    {
        var o = OverlayRect();
        var wa = Work(o.Location);
        var sz = TabSize();
        int y = Math.Clamp(o.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - sz.Height));
        return new Rectangle(wa.Right - sz.Width, y, sz.Width, sz.Height);
    }

    RectangleF BtnRect(int i) => new(pad + (Cols - 6 + i * 3) * crt.CW, pad, 3 * crt.CW, crt.CH);

    /// <summary>Clickable area: the button plus some slack, split down the middle between the two.</summary>
    int HitBtn(Point p)
    {
        if (tab) return -1;
        var a = BtnRect(0);
        var hit = RectangleF.FromLTRB(a.Left - crt.CW, 0, Width, a.Bottom + crt.CH * 0.6f);
        if (!hit.Contains(p)) return -1;
        return p.X < BtnRect(1).Left ? 0 : 1;
    }

    // ---------- animation ----------

    void Animate(Rectangle from, Rectangle to, Action? done)
    {
        aFrom = from;
        aTo = to;
        aDone = done;
        Bounds = from;
        sw.Restart();
        anim.Start();
    }

    void Step()
    {
        double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / AnimMs);
        double e = 1 - Math.Pow(1 - t, 3);
        Bounds = new Rectangle(Lerp(aFrom.X, aTo.X, e), Lerp(aFrom.Y, aTo.Y, e), Lerp(aFrom.Width, aTo.Width, e), Lerp(aFrom.Height, aTo.Height, e));
        if (t < 1) return;
        anim.Stop();
        var d = aDone;
        aDone = null;
        d?.Invoke();
    }

    static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    // ---------- content ----------

    void BuildBody()
    {
        body.Clear();
        int w = Cols;
        var pins = new List<(string title, double p, bool complete, TodoItem? next)>();
        foreach (var sec in Store.Sections)
        {
            if (Store.Pinned.Contains("S:" + sec.Id))
                pins.Add((sec.Title.ToUpperInvariant(), sec.Progress(), sec.Items.Count > 0 && sec.Items.All(i => i.Complete()), sec.NextOpenLeaf()));
            foreach (var it in sec.All())
                if (Store.Pinned.Contains(it.Id))
                    pins.Add((it.Text, it.Progress(), it.Complete(), it.Children.Count > 0 ? it.NextOpenLeaf() : null));
        }

        if (Store.LastError != null)
            body.Add(("!! " + Theme.Fit(Store.LastError, w - 3), Theme.Amber));

        // Linked files that can't be read right now (shown from their last copy, retried automatically)
        foreach (var sec in Store.Linked.Where(x => x.Error != null))
            body.Add(("!! " + Theme.Fit($"{sec.Title.ToUpperInvariant()}: {sec.Error}", w - 3), Theme.Amber));

        // Today: due today / overdue / marked with T
        var now = DateTime.Now;
        var today = Store.TodayItems(now);
        if (today.Count > 0)
        {
            body.Add(("☼ TODAY " + new string('─', w - 8), Theme.Fg));
            foreach (var it in today.Take(8))
            {
                bool overdue = Dates.IsOverdue(it, now);
                string tag = Dates.TryParseStored(it.Due, out var d, out var ht) ? Dates.Label(d, ht, now, false, compact: true) : "";
                string box = it.Progress() > 0 ? "[~] " : "[ ] ";
                int room = w - box.Length - (tag.Length > 0 ? tag.Length + 1 : 0);
                body.Add((box + Theme.Fit(it.Text, room).PadRight(room) + (tag.Length > 0 ? " " + tag : ""), overdue ? Theme.Amber : Theme.Fg));
            }
            if (today.Count > 8) body.Add(($"    +{today.Count - 8} MORE TODAY", Theme.Dim));
            if (pins.Count == 0) return;
            body.Add(("■ PINNED " + new string('─', w - 9), Theme.Fg));
        }

        if (pins.Count == 0)
        {
            body.Add(("NO PINNED TODOS.", Theme.Dim));
            body.Add(("Open the full view, select, press P.", Theme.Dim));
            foreach (var sec in Store.Sections)
            {
                if (sec.Items.Count == 0) continue;
                body.Add(("", Theme.Dim));
                SummaryLine(sec.Title.ToUpperInvariant(), sec.Progress(), Theme.Fg);
            }
            return;
        }

        int shown = 0;
        foreach (var p in pins)
        {
            if (body.Count > 36)
            {
                body.Add(($"+{pins.Count - shown} MORE…", Theme.Dim));
                break;
            }
            var c = p.complete ? Theme.Dim : Theme.Fg;
            string box = p.complete ? "[X] " : p.p > 0 ? "[~] " : "[ ] ";
            int room = w - box.Length - 5;
            body.Add((box + Theme.Fit(p.title, room).PadRight(room) + " " + Theme.Pct(p.p), c));
            body.Add(("    " + Theme.Bar(p.p, w - 4), c));
            if (p.next != null) body.Add(("    » " + Theme.Fit(p.next.Text, w - 6), Theme.Dim));
            shown++;
        }
    }

    void SummaryLine(string title, double p, Color c)
    {
        int room = Cols - 5;
        body.Add((Theme.Fit(title, room).PadRight(room) + " " + Theme.Pct(p), c));
        body.Add((Theme.Bar(p, Cols), c));
    }

    // ---------- painting ----------

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        crt.Effects = Store.Settings.Crt;
        if (tab)
        {
            PaintTab(e.Graphics);
            return;
        }

        var g = crt.BeginLayer(ClientSize);
        double total = Store.TotalProgress();
        crt.Text(g, pad, pad, "▓ TODO " + Theme.Pct(total).Trim(), Theme.Fg);
        DrawBtn(g, 0, "[≡]");
        DrawBtn(g, 1, "[»]");
        crt.Text(g, pad, pad + crt.CH, Theme.Bar(total, Cols), Theme.Dim);
        for (int i = 0; i < body.Count; i++)
            crt.Text(g, pad, pad + (i + 2) * crt.CH, body[i].text, body[i].color);
        crt.EndLayer(g);

        crt.Present(e.Graphics, ClientRectangle, ClientRectangle, false, 0);
        using var pen = new Pen(Theme.Dim);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    void DrawBtn(Graphics g, int i, string label)
    {
        var r = BtnRect(i);
        if (hoverBtn == i)
        {
            g.FillRectangle(crt.Brush(Theme.Fg), r);
            crt.Text(g, r.X, r.Y, label, Theme.Bg);
        }
        else crt.Text(g, r.X, r.Y, label, Theme.Fg);
    }

    void PaintTab(Graphics g)
    {
        g.Clear(Theme.Bg);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var pen = new Pen(Theme.Dim)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        crt.Text(g, (Width - crt.CW) / 2f, pad, "◄", Theme.Fg);

        string label = "TODO " + Theme.Pct(Store.TotalProgress()).Trim();
        var st = g.Save();
        g.TranslateTransform(Width / 2f, Height / 2f + crt.CH / 2f);
        g.RotateTransform(-90);
        crt.Text(g, -label.Length * crt.CW / 2f, -crt.CH / 2f, label, Theme.Fg);
        g.Restore(st);
    }

    // ---------- mouse ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || anim.Enabled) return;
        down = true;
        moved = pulling = false;
        downAt = Cursor.Position;
        downLoc = Location;
        onHeader = e.Y < pad + crt.CH * 2;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!down)
        {
            int hb = HitBtn(e.Location);
            if (hb != hoverBtn) { hoverBtn = hb; Invalidate(); }
            Cursor = tab || hb >= 0 ? Cursors.Hand : e.Y < pad + crt.CH * 2 ? Cursors.SizeAll : Cursors.Default;
            return;
        }

        var p = Cursor.Position;
        int dx = p.X - downAt.X, dy = p.Y - downAt.Y;
        if (!moved && Math.Abs(dx) + Math.Abs(dy) < 6) return;

        if (tab && !moved)
        {
            // start pulling the overlay out of the screen edge; it follows the mouse
            moved = pulling = true;
            tab = false;
            BuildBody();
            var sz = OverlaySize();
            grab = new Point((int)(14 * s), Math.Clamp(p.Y - Top, (int)(8 * s), Math.Max((int)(8 * s), sz.Height - (int)(8 * s))));
            Bounds = new Rectangle(p.X - grab.X, p.Y - grab.Y, sz.Width, sz.Height);
            Invalidate();
            return;
        }

        moved = true;
        if (pulling) Location = new Point(p.X - grab.X, p.Y - grab.Y);
        else if (!tab && onHeader && hoverBtn < 0) Location = new Point(downLoc.X + dx, downLoc.Y + dy);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!down) return;
        down = false;
        Capture = false;

        if (pulling)
        {
            pulling = false;
            Settle();
            app.PulledOut();
            return;
        }
        if (tab)
        {
            if (!moved) app.GoOverlay(); // click on the tab
            return;
        }
        if (moved)
        {
            Settle();
            return;
        }
        switch (HitBtn(e.Location))
        {
            case 0: app.GoFull(); break;
            case 1: app.GoSide(); break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverBtn != -1) { hoverBtn = -1; Invalidate(); }
    }

    /// <summary>Keep the overlay on screen and remember where the user put it.</summary>
    void Settle()
    {
        var r = Clamp(Bounds);
        Store.Settings.OverlayX = r.X;
        Store.Settings.OverlayY = r.Y;
        Store.Save();
        if (r != Bounds) Animate(Bounds, r, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            anim.Dispose();
            crt.Dispose();
        }
        base.Dispose(disposing);
    }
}
