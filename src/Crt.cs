using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace RetroTodo;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(4, 14, 7);
    public static readonly Color Fg = Color.FromArgb(70, 245, 120);
    public static readonly Color Dim = Color.FromArgb(34, 140, 68);
    public static readonly Color Faint = Color.FromArgb(18, 64, 32);
    public static readonly Color Amber = Color.FromArgb(255, 182, 40);
    public static readonly Color Bezel = Color.FromArgb(14, 16, 14);

    /// <summary>Floors so nothing shows 100% until it really is done.</summary>
    public static string Pct(double p) => $"{(int)Math.Floor(p * 100 + 1e-9),3}%";

    public static string Bar(double p, int width)
    {
        if (width <= 0) return "";
        int n = Math.Clamp((int)Math.Floor(p * width + 1e-9), 0, width);
        return new string('█', n) + new string('░', width - n);
    }

    public static string Fit(string s, int width)
    {
        if (width <= 0) return "";
        if (s.Length <= width) return s;
        return width == 1 ? s[..1] : s[..(width - 1)] + "…";
    }

    public static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Bg)) g.FillRectangle(bg, 1, 3, 30, 26);
            using (var pen = new Pen(Dim, 2)) g.DrawRectangle(pen, 2, 4, 28, 24);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var f = new Font("Consolas", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var b = new SolidBrush(Fg);
            g.DrawString(">_", f, b, 4, 7);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>
/// Draws a monospace text "screen" with a cheap CRT look: phosphor glow, scanlines, vignette.
/// Text is drawn into a transparent layer; the glow is that layer shrunk 4x and stretched back.
/// Nothing animates, so it only costs CPU when something changes.
/// </summary>
sealed class Crt : IDisposable
{
    public readonly Font Font;
    public readonly float CW, CH;
    public bool Effects = true;

    Bitmap? layer, glow;
    readonly Dictionary<int, SolidBrush> brushes = new();
    readonly TextureBrush scan;
    readonly ImageAttributes glowAttr = new();
    static readonly StringFormat Fmt = MakeFormat();

    public Size LayerSize => layer?.Size ?? Size.Empty;

    public Crt(float px)
    {
        Font = new Font("Consolas", px, FontStyle.Regular, GraphicsUnit.Pixel);
        using (var b = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(b))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            CW = g.MeasureString(new string('M', 40), Font, PointF.Empty, Fmt).Width / 40f;
            CH = MathF.Ceiling(Font.GetHeight(g) * 1.15f);
        }
        using (var sb = new Bitmap(2, 3, PixelFormat.Format32bppArgb))
        {
            sb.SetPixel(0, 2, Color.FromArgb(85, 0, 0, 0));
            sb.SetPixel(1, 2, Color.FromArgb(85, 0, 0, 0));
            scan = new TextureBrush(sb, WrapMode.Tile);
        }
        glowAttr.SetColorMatrix(new ColorMatrix { Matrix33 = 0.65f });
    }

    static StringFormat MakeFormat()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    public SolidBrush Brush(Color c)
    {
        int k = c.ToArgb();
        if (!brushes.TryGetValue(k, out var b)) brushes[k] = b = new SolidBrush(c);
        return b;
    }

    /// <summary>
    /// Draws text on the character grid. Block characters (█▓▒░) are not in Consolas, and a
    /// fallback font would break the grid, so those cells are painted as rectangles.
    /// </summary>
    public void Text(Graphics g, float x, float y, string s, Color c)
    {
        int start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            int alpha = i < s.Length ? Shade(s[i]) : -1;
            if (i < s.Length && alpha < 0) continue;
            if (i > start) g.DrawString(s.Substring(start, i - start), Font, Brush(c), x + start * CW, y + 1, Fmt);
            if (alpha > 0) g.FillRectangle(Brush(Color.FromArgb(alpha * c.A / 255, c)), x + i * CW, y + CH * 0.18f, CW, CH * 0.64f);
            start = i + 1;
        }
    }

    static int Shade(char ch) => ch switch
    {
        '█' => 255,
        '▓' => 175,
        '▒' => 110,
        '░' => 50,
        _ => -1,
    };

    public Graphics BeginLayer(Size size)
    {
        size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (layer == null || layer.Size != size)
        {
            layer?.Dispose();
            glow?.Dispose();
            layer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            glow = new Bitmap(Math.Max(1, size.Width / 4), Math.Max(1, size.Height / 4), PixelFormat.Format32bppPArgb);
        }
        var g = Graphics.FromImage(layer);
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    public void EndLayer(Graphics g)
    {
        g.Dispose();
        if (!Effects || glow == null || layer == null) return;
        using var gg = Graphics.FromImage(glow);
        gg.Clear(Color.Transparent);
        gg.InterpolationMode = InterpolationMode.HighQualityBilinear;
        gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
        gg.DrawImage(layer, new Rectangle(0, 0, glow.Width, glow.Height));
    }

    public void Present(Graphics g, Rectangle client, Rectangle screen, bool bezel, int radius)
    {
        if (bezel) g.FillRectangle(Brush(Theme.Bezel), client);

        GraphicsPath? path = radius > 0 ? Rounded(screen, radius) : null;
        var state = g.Save();
        if (path != null) g.SetClip(path, CombineMode.Intersect);

        g.FillRectangle(Brush(Theme.Bg), screen);
        if (Effects && glow != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(glow, screen, 0, 0, glow.Width, glow.Height, GraphicsUnit.Pixel, glowAttr);
        }
        if (layer != null)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(layer, new Rectangle(screen.Location, layer.Size), 0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel);
        }
        if (Effects)
        {
            scan.ResetTransform();
            scan.TranslateTransform(screen.X, screen.Y);
            g.FillRectangle(scan, screen);
            Vignette(g, screen);
        }
        g.Restore(state);

        if (path != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(45, Theme.Fg), 1.5f);
            g.DrawPath(pen, path);
            g.SmoothingMode = SmoothingMode.Default;
            path.Dispose();
        }
    }

    static void Vignette(Graphics g, Rectangle r)
    {
        using var p = new GraphicsPath();
        p.AddEllipse(RectangleF.Inflate(r, r.Width * 0.2f, r.Height * 0.25f));
        using var b = new PathGradientBrush(p)
        {
            CenterColor = Color.FromArgb(0, 0, 0, 0),
            SurroundColors = new[] { Color.FromArgb(210, 0, 0, 0) },
            FocusScales = new PointF(0.62f, 0.55f),
        };
        g.FillRectangle(b, r);
    }

    static GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        Font.Dispose();
        layer?.Dispose();
        glow?.Dispose();
        scan.Dispose();
        glowAttr.Dispose();
        foreach (var b in brushes.Values) b.Dispose();
        brushes.Clear();
    }
}
