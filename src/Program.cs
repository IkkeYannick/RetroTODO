using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RetroTodo;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var a = Args.Parse(args);
        using var mutex = new Mutex(true, @"Local\RetroTodo." + Sources.Hash(Paths.Dir.ToLowerInvariant()), out bool first);
        if (!first)
        {
            // Already running: hand the request to the running copy and exit.
            Inbox.Send(a);
            return;
        }
        if (a.Quit) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new AppController(a));
        GC.KeepAlive(mutex);
    }
}

sealed class Args
{
    public bool Startup, Quit;
    public readonly List<string> Links = new();

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (int i = 0; i < args.Length; i++)
        {
            string s = args[i];
            switch (s.ToLowerInvariant())
            {
                case "--startup": a.Startup = true; break;
                case "--quit": a.Quit = true; break;
                case "--link":
                    if (i + 1 < args.Length) a.Links.Add(Full(args[++i]));
                    break;
                default:
                    if (!s.StartsWith("--")) a.Links.Add(Full(s)); // e.g. a file dropped on the exe
                    break;
            }
        }
        return a;
    }

    static string Full(string p)
    {
        try { return Path.GetFullPath(p.Trim().Trim('"')); } catch { return p; }
    }
}

static class Paths
{
    public static readonly string Dir =
        Environment.GetEnvironmentVariable("RETROTODO_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroTodo");

    public static readonly string Data = Path.Combine(Dir, "todos.json");
    public static readonly string Inbox = Path.Combine(Dir, "inbox");
    public static readonly string Cache = Path.Combine(Dir, "cache");
}

/// <summary>A second launch writes a small .cmd file here; the running copy picks it up.</summary>
static class Inbox
{
    public static void Send(Args a)
    {
        var sb = new StringBuilder();
        if (a.Quit) sb.AppendLine("quit");
        foreach (var l in a.Links) sb.Append("link\t").AppendLine(l);
        if (sb.Length == 0) sb.AppendLine("show");
        Directory.CreateDirectory(Paths.Inbox);
        string name = Path.Combine(Paths.Inbox, Guid.NewGuid().ToString("N"));
        File.WriteAllText(name + ".tmp", sb.ToString());
        File.Move(name + ".tmp", name + ".cmd");
    }
}

static class AutoStart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "RetroTodo";
    static string Command => LaunchCommand("--startup");

    /// <summary>
    /// Command line that starts this app. When it runs as "dotnet RetroTodo.dll" (the install layout,
    /// because Smart App Control blocks unsigned .exe files) it goes through "conhost --headless"
    /// so dotnet.exe's console window never appears.
    /// </summary>
    public static string LaunchCommand(string args)
    {
        string host = Environment.ProcessPath ?? "";
        if (Path.GetFileName(host).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "RetroTodo.dll");
            string conhost = Path.Combine(Environment.SystemDirectory, "conhost.exe");
            return $"\"{conhost}\" --headless \"{host}\" \"{dll}\" {args}".TrimEnd();
        }
        return $"\"{host}\" {args}".TrimEnd();
    }

    public static bool IsOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) is string;
    }

    public static bool PointsHere()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return string.Equals(k?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, Command);
        else k.DeleteValue(Name, false);
    }
}

/// <summary>Parses "Win+Shift+T", "Ctrl+Alt+T", "Ctrl+Shift+Space", "Alt+F9" into RegisterHotKey values.</summary>
static class Hotkeys
{
    public static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "win" or "windows": mods |= 0x8; break;
                case "shift": mods |= 0x4; break;
                case "ctrl" or "control": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                default: return false;
            }
        }
        string k = parts[^1];
        if (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0])) vk = char.ToUpperInvariant(k[0]);
        else if (k.Equals("space", StringComparison.OrdinalIgnoreCase)) vk = 0x20;
        else if (Enum.TryParse<Keys>(k, true, out var key) && key is >= Keys.F1 and <= Keys.F24) vk = (uint)key;
        else return false;
        return mods != 0;
    }
}

static class Native
{
    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);

    /// <summary>
    /// The overlay never takes focus, so Windows would refuse to focus the full view when you
    /// click [≡]. Borrowing the foreground thread's input state lets the window come forward.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attach = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attach) AttachThreadInput(me, fg, false);
        }
        catch { }
    }

    /// <summary>Give unused memory back to Windows while the app sits in the background.</summary>
    public static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); } catch { }
    }

    /// <summary>Dark, green-tinted title bar on Windows 11.</summary>
    public static void DarkTitle(IntPtr hwnd)
    {
        try
        {
            int on = 1; DwmSetWindowAttribute(hwnd, 20, ref on, 4);
            int caption = 0x00080E08; DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            int text = 0x0078F546; DwmSetWindowAttribute(hwnd, 36, ref text, 4);
        }
        catch { }
    }
}
