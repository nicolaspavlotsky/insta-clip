using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace InstaClip;

/// <summary>
/// The "● REC 1:23" badge in a corner of the recorded monitor. Click-through, never takes focus, and excluded from
/// screen capture, so you see it but the clip doesn't.
/// </summary>
sealed class Overlay : Form
{
    static readonly Color Bg = Color.FromArgb(22, 22, 26), Red = Color.FromArgb(255, 59, 74), DimRed = Color.FromArgb(110, 40, 46), Fg = Color.FromArgb(238, 238, 242);
    static readonly IntPtr HwndTopmost = new(-1);
    const int SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    readonly Stopwatch clock = new();
    string status;   // shown instead of the timer while starting / saving
    bool blink;
    float scale = 1;
    Font font;

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Bg;
        DoubleBuffered = true;
        timer.Tick += (_, _) => { blink = !blink; KeepOnTop(); Invalidate(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08 | 0x20 | 0x80 | 0x08000000;   // WS_EX_TOPMOST | WS_EX_TRANSPARENT (click-through) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, 0x11);   // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+)
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x02E0) return;   // WM_DPICHANGED: the size is set per monitor in ShowOn, don't let WinForms rescale it
        base.WndProc(ref m);
    }

    /// <summary>Shows the badge on <paramref name="d"/> with a status text (e.g. "Starting…"). corner "off" keeps it hidden.</summary>
    public void ShowOn(Display d, string corner, string text)
    {
        status = text;
        clock.Reset();
        if (corner == "off") return;
        scale = GetDpiForMonitor(d.Monitor, 0, out var dpi, out _) == 0 ? dpi / 96f : DeviceDpi / 96f;
        font?.Dispose();
        font = new Font("Segoe UI Semibold", 13 * scale, GraphicsUnit.Pixel);
        int w = (int)(118 * scale), h = (int)(30 * scale), m = (int)(16 * scale);
        var area = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == d.DeviceName)?.WorkingArea ?? d.Bounds;
        int x = corner.EndsWith("left") ? area.Left + m : area.Right - w - m;
        int y = corner.StartsWith("bottom") ? area.Bottom - h - m : area.Top + m;
        var old = Region;
        using (var path = Pill(w, h)) Region = new Region(path);
        old?.Dispose();
        Bounds = new Rectangle(x, y, w, h);
        if (!Visible) Show();
        SetWindowPos(Handle, HwndTopmost, x, y, w, h, SwpNoActivate | SwpShowWindow);
        timer.Start();
        Invalidate();
    }

    public void SetStatus(string text)
    {
        status = text;
        Invalidate();
    }

    public void StartClock()
    {
        status = null;
        blink = false;
        clock.Restart();
        Invalidate();
    }

    public void HideBadge()
    {
        timer.Stop();
        clock.Reset();
        if (Visible) Hide();
    }

    // Games and other topmost windows can end up above us; push back to the top every tick
    void KeepOnTop()
    {
        if (Visible) SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    static GraphicsPath Pill(int w, int h)
    {
        var p = new GraphicsPath();
        p.AddArc(0, 0, h, h, 90, 180);
        p.AddArc(w - h - 1, 0, h, h, 270, 180);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (font == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float s = scale, dot = 10 * s, left = 12 * s, cy = Height / 2f;
        bool rec = status == null;
        using (var b = new SolidBrush(rec && blink ? DimRed : Red))
            g.FillEllipse(b, left, cy - dot / 2, dot, dot);
        var t = clock.Elapsed;
        var text = rec ? "REC " + (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss")) : status;
        var size = g.MeasureString(text, font);
        using var fg = new SolidBrush(Fg);
        g.DrawString(text, font, fg, left + dot + 7 * s, cy - size.Height / 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); font?.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
