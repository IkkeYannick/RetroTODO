namespace RetroTodo;

/// <summary>1 = Full window, 2 = Overlay, 3 = Side tab.</summary>
public enum Mode { None, Full, Overlay, Side }

/// <summary>
/// Owns the three views and enforces the allowed moves:
///   Full -> Overlay, Full -> Side, Overlay -> Full, Overlay -> Side, Side -> Overlay.
///   (Side -> Full is not allowed.)
/// Also owns the tray icon, reminders, the quick-add hotkey and the inbox for second launches.
/// </summary>
sealed class AppController : ApplicationContext
{
    const string FallbackHotkey = "Ctrl+Alt+T";

    public readonly Store Store;
    public readonly Icon AppIcon;
    public Mode Mode { get; private set; }
    public string HotkeyStatus { get; private set; } = "";

    readonly OverlayForm overlay;
    MainForm? main;
    QuickAddForm? quick;
    readonly NotifyIcon tray;
    readonly Reminders reminders;
    readonly FileSystemWatcher? inbox;
    readonly System.Windows.Forms.Timer trimTimer = new() { Interval = 1500 };

    public AppController(Args args)
    {
        AppIcon = Theme.MakeIcon();
        Store = Store.Load();

        overlay = new OverlayForm(this);
        _ = overlay.Handle; // UI-thread target for file watcher events and the hotkey
        Store.StartWatching(overlay);

        tray = new NotifyIcon { Icon = AppIcon, Text = "Retro Todo", Visible = true, ContextMenuStrip = BuildMenu() };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Summon(); };
        tray.BalloonTipClicked += (_, _) => { if (Mode == Mode.Side) GoOverlay(); else GoFull(); };

        reminders = new Reminders(Store, Notify, RefreshViews);
        Store.Changed += () =>
        {
            RefreshViews();
            reminders.Reschedule();
        };

        overlay.HotkeyPressed += QuickAdd;
        if (!overlay.SetHotkey(Store.Settings.Hotkey))
        {
            bool fallback = overlay.SetHotkey(FallbackHotkey);
            HotkeyStatus = fallback
                ? $"{Store.Settings.Hotkey.ToUpperInvariant()} IS TAKEN (WINDOWS OR ANOTHER APP). QUICK ADD IS ON {FallbackHotkey.ToUpperInvariant()}."
                : $"QUICK ADD HOTKEY {Store.Settings.Hotkey.ToUpperInvariant()} IS TAKEN. PICK ANOTHER WITH :hotkey";
            Notify("QUICK ADD HOTKEY", HotkeyStatus);
        }
        else HotkeyStatus = $"QUICK ADD: {Store.Settings.Hotkey.ToUpperInvariant()}";

        trimTimer.Tick += (_, _) => { trimTimer.Stop(); Native.TrimMemory(); };

        foreach (var l in args.Links) Notice(Store.Link(l) ?? "LINKED " + Path.GetFileName(l));

        try
        {
            Directory.CreateDirectory(Paths.Inbox);
            inbox = new FileSystemWatcher(Paths.Inbox, "*.cmd")
            {
                SynchronizingObject = overlay,
                NotifyFilter = NotifyFilters.FileName,
                InternalBufferSize = 4096,
            };
            inbox.Created += (_, _) => ProcessInbox(false);
            inbox.Renamed += (_, _) => ProcessInbox(false);
            inbox.EnableRaisingEvents = true;
        }
        catch { }
        ProcessInbox(true);

        if (args.Startup) GoOverlay();
        else GoFull();
    }

    void RefreshViews()
    {
        overlay.RefreshContent();
        main?.RefreshContent();
    }

    // ---------- mode transitions ----------

    public void GoFull()
    {
        if (Mode == Mode.Side) return; // 3 -> 1 is not allowed; go through the overlay
        overlay.HideAll();
        if (main == null)
        {
            main = new MainForm(this);
            main.FormClosed += (_, _) => main = null;
        }
        Mode = Mode.Full;
        main.Show();
        if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
        Native.ForceForeground(main.Handle);
        main.Activate();
        main.Focus();
    }

    public void GoOverlay()
    {
        bool fromSide = Mode == Mode.Side;
        Mode = Mode.Overlay;
        CloseMain();
        overlay.ShowOverlay(fromSide);
        TrimSoon();
    }

    public void GoSide()
    {
        bool fromOverlay = Mode == Mode.Overlay;
        Mode = Mode.Side;
        CloseMain();
        overlay.SlideAway(fromOverlay);
        TrimSoon();
    }

    /// <summary>The side tab was dragged out into the overlay (3 -> 2).</summary>
    public void PulledOut()
    {
        Mode = Mode.Overlay;
        TrimSoon();
    }

    /// <summary>Tray click / launching the exe again: step one view "up".</summary>
    void Summon()
    {
        if (Mode == Mode.Side) GoOverlay();
        else GoFull();
    }

    void CloseMain()
    {
        if (main == null) return;
        var m = main;
        main = null;
        m.AllowClose = true;
        m.Close(); // frees the window and its screen buffers
    }

    void TrimSoon()
    {
        trimTimer.Stop();
        trimTimer.Start();
    }

    public void Quit()
    {
        main?.SaveWindow();
        Store.Save();
        tray.Visible = false;
        ExitThread();
    }

    // ---------- quick add, hotkey, notifications ----------

    public void QuickAdd()
    {
        if (quick == null)
        {
            quick = new QuickAddForm(this);
            quick.FormClosed += (_, _) =>
            {
                quick = null;
                if (Mode != Mode.Full) TrimSoon();
            };
            quick.Show();
        }
        Native.ForceForeground(quick.Handle);
        quick.Activate();
        quick.Focus();
    }

    /// <summary>Change the quick-add hotkey. Keeps the old one if the new one can't be registered.</summary>
    public string SetHotkey(string spec)
    {
        if (!Hotkeys.TryParse(spec, out _, out _))
            return $"CAN'T READ '{spec}'. EXAMPLES: Win+Shift+T, Ctrl+Alt+T, Ctrl+Shift+Space";
        if (!overlay.SetHotkey(spec))
            return $"{spec.ToUpperInvariant()} IS TAKEN BY WINDOWS OR ANOTHER APP. KEPT {overlay.ActiveHotkey?.ToUpperInvariant() ?? "NONE"}.";
        Store.Settings.Hotkey = spec;
        Store.Save();
        HotkeyStatus = $"QUICK ADD: {spec.ToUpperInvariant()}";
        return HotkeyStatus;
    }

    public void ShowDigest() => reminders.Digest(DateTime.Now);

    void Notify(string title, string text)
    {
        if (text.Length > 250) text = text[..249] + "…";
        if (title.Length > 60) title = title[..59] + "…";
        tray.ShowBalloonTip(10000, title, text, ToolTipIcon.None);
    }

    void Notice(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        if (main != null) main.Notice(msg);
        else Notify("Retro Todo", msg);
    }

    void ProcessInbox(bool startup)
    {
        string[] files;
        try { files = Directory.GetFiles(Paths.Inbox, "*.cmd"); }
        catch { return; }

        foreach (var f in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(f);
                File.Delete(f);
            }
            catch { continue; }

            foreach (var line in lines)
            {
                var parts = line.Split('\t', 2);
                switch (parts[0].Trim())
                {
                    case "link" when parts.Length == 2:
                        Notice(Store.Link(parts[1]) ?? "LINKED " + Path.GetFileName(parts[1]));
                        break;
                    case "show" when !startup:
                        Summon();
                        break;
                    case "quit" when !startup:
                        Quit();
                        return;
                }
            }
        }
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowImageMargin = false };
        var full = new ToolStripMenuItem("Open full view", null, (_, _) => GoFull());
        m.Items.Add(full);
        m.Items.Add("Show overlay", null, (_, _) => GoOverlay());
        m.Items.Add("Slide away", null, (_, _) => GoSide());
        m.Items.Add("Quick add…", null, (_, _) => QuickAdd());
        m.Items.Add("Today's summary", null, (_, _) => ShowDigest());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quit", null, (_, _) => Quit());
        m.Opening += (_, _) => full.Enabled = Mode != Mode.Side;
        return m;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Dispose();
            inbox?.Dispose();
            trimTimer.Dispose();
            reminders.Dispose();
        }
        base.Dispose(disposing);
    }
}
