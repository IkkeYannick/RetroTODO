namespace RetroTodo;

/// <summary>Small popup from the global hotkey: type a todo (optionally "@fri 18:00"), Enter saves to LOCAL.</summary>
sealed class QuickAddForm : Form
{
    const int Cols = 64;
    const int WS_EX_TOOLWINDOW = 0x80;
    const string Tip = "add a date with @   @today 18:00   @fri   @20/11   @+2h";

    readonly AppController app;
    readonly Crt crt;
    readonly int pad;
    string text = "", status = "";
    int caret;
    bool statusError, closing;

    public QuickAddForm(AppController app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Retro Todo quick add";
        BackColor = Theme.Bg;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        float s = DeviceDpi / 96f;
        pad = (int)(12 * s);
        crt = new Crt(16f * s) { Effects = app.Store.Settings.Crt };
        var size = new Size((int)Math.Ceiling(Cols * crt.CW) + pad * 2, (int)Math.Ceiling(4 * crt.CH) + pad * 2);
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Bounds = new Rectangle(wa.X + (wa.Width - size.Width) / 2, wa.Y + wa.Height / 4, size.Width, size.Height);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (!Focused) Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        CloseSoon(); // clicking elsewhere cancels, like a launcher
    }

    void CloseSoon()
    {
        if (closing) return;
        closing = true;
        BeginInvoke(new Action(Close));
    }

    (string visible, int caretCol) VisibleText()
    {
        int room = Cols - 3;
        int start = Math.Max(0, caret - room + 1);
        return (text.Substring(start, Math.Min(room, text.Length - start)), caret - start);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = crt.BeginLayer(ClientSize);
        string right = "ENTER save · ESC cancel";
        crt.Text(g, pad, pad, "▓ QUICK ADD → LOCAL", Theme.Fg);
        crt.Text(g, pad + (Cols - right.Length) * crt.CW, pad, right, Theme.Dim);
        crt.Text(g, pad, pad + crt.CH, new string('═', Cols), Theme.Dim);
        var (vis, cc) = VisibleText();
        crt.Text(g, pad, pad + 2 * crt.CH, "> " + vis, Theme.Fg);
        crt.Text(g, pad, pad + 3 * crt.CH, Theme.Fit(status.Length > 0 ? status : Tip, Cols), statusError ? Theme.Amber : Theme.Dim);
        crt.EndLayer(g);
        crt.Present(e.Graphics, ClientRectangle, ClientRectangle, false, 0);

        float x = pad + (2 + cc) * crt.CW, y = pad + 2 * crt.CH;
        e.Graphics.FillRectangle(crt.Brush(Theme.Fg), x, y + 1, crt.CW, crt.CH - 2);
        if (caret < text.Length) crt.Text(e.Graphics, x, y, text[caret].ToString(), Theme.Bg);
        using var pen = new Pen(Theme.Fg);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys code = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0;
        switch (code)
        {
            case Keys.Enter: Submit(); return true;
            case Keys.Escape: CloseSoon(); return true;
            case Keys.Back:
                if (caret > 0) { text = text.Remove(caret - 1, 1); caret--; }
                break;
            case Keys.Delete:
                if (caret < text.Length) text = text.Remove(caret, 1);
                break;
            case Keys.Left: caret = Math.Max(0, caret - 1); break;
            case Keys.Right: caret = Math.Min(text.Length, caret + 1); break;
            case Keys.Home: caret = 0; break;
            case Keys.End: caret = text.Length; break;
            case Keys.V when ctrl:
                if (Clipboard.ContainsText()) Insert(Clipboard.GetText());
                break;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
        status = "";
        Invalidate();
        return true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        e.Handled = true;
        if (char.IsControl(e.KeyChar)) return;
        Insert(e.KeyChar.ToString());
        status = "";
        Invalidate();
    }

    void Insert(string t)
    {
        t = t.Replace("\r", "").Replace('\n', ' ').Replace('\t', ' ');
        text = text.Insert(caret, t);
        caret += t.Length;
    }

    void Submit()
    {
        if (string.IsNullOrWhiteSpace(text)) { CloseSoon(); return; }
        Dates.SplitDue(text, DateTime.Now, out var t, out var due, out var err);
        if (err != null || t.Length == 0)
        {
            status = err != null ? err + " FIX IT OR REMOVE THE @." : "TYPE THE TODO BEFORE THE @DATE.";
            statusError = true;
            Invalidate();
            return;
        }
        var store = app.Store;
        store.Add(store.Local, null, store.Local.Items.Count, t, due);
        CloseSoon();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) crt.Dispose();
        base.Dispose(disposing);
    }
}
