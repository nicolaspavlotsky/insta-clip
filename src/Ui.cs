using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;

namespace InstaClip;

static class Hotkey
{
    public const int Alt = 1, Ctrl = 2, Shift = 4;   // RegisterHotKey's MOD_* values

    public static string Format(int mods, int key)
    {
        var parts = new List<string>();
        if ((mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & Alt) != 0) parts.Add("Alt");
        if ((mods & Shift) != 0) parts.Add("Shift");
        var k = (Keys)key;
        string name;
        if (k >= Keys.D0 && k <= Keys.D9) name = ((char)('0' + (k - Keys.D0))).ToString();
        else if (k >= Keys.NumPad0 && k <= Keys.NumPad9) name = "Num " + (k - Keys.NumPad0);
        else name = k switch
        {
            Keys.Oemtilde => "`", Keys.OemMinus => "-", Keys.Oemplus => "=", Keys.Oemcomma => ",", Keys.OemPeriod => ".",
            Keys.OemQuestion => "/", Keys.OemSemicolon => ";", Keys.OemQuotes => "'", Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]", Keys.OemPipe => "\\", Keys.PageUp => "PageUp", Keys.PageDown => "PageDown",
            Keys.Snapshot => "PrintScreen",
            _ => k.ToString(),
        };
        parts.Add(name);
        return string.Join("+", parts);
    }
}

/// <summary>Hidden window that owns the global shortcut and raises <see cref="Pressed"/> from anywhere, games included.</summary>
sealed class HotkeyWindow : NativeWindow
{
    const int WmHotkey = 0x0312, ModNoRepeat = 0x4000, Id = 1;
    bool registered;

    public event Action Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public bool Register(int mods, int key)
    {
        Unregister();
        registered = RegisterHotKey(Handle, Id, mods | ModNoRepeat, key);
        return registered;
    }

    public void Unregister()
    {
        if (registered) UnregisterHotKey(Handle, Id);
        registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>"Press the new shortcut" box. Needs a modifier, except for F-keys which work alone.</summary>
sealed class HotkeyDialog : Form
{
    public int Mods { get; private set; }
    public int Key { get; private set; }

    public HotkeyDialog()
    {
        Text = "InstaClip shortcut";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.FromArgb(232, 232, 236);
        Font = new Font("Segoe UI", 10f);
        float s = DeviceDpi / 96f;
        ClientSize = new Size((int)(380 * s), (int)(120 * s));
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "Press the new shortcut\r\n(for example Alt+A, Ctrl+Shift+R or F9)\r\n\r\nEsc to cancel",
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        int mods = ((keyData & Keys.Control) != 0 ? Hotkey.Ctrl : 0) | ((keyData & Keys.Alt) != 0 ? Hotkey.Alt : 0) | ((keyData & Keys.Shift) != 0 ? Hotkey.Shift : 0);
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true;
        if (key == Keys.Escape && mods == 0) { DialogResult = DialogResult.Cancel; return true; }
        bool fkey = key >= Keys.F1 && key <= Keys.F24;
        if ((mods == 0 && !fkey) || (mods == Hotkey.Alt && key == Keys.F4)) return true;
        Mods = mods;
        Key = (int)key;
        DialogResult = DialogResult.OK;
        return true;
    }
}

/// <summary>Dark tray menu to match Clipper.</summary>
sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkColors())
{
    static readonly Color Text = Color.FromArgb(232, 232, 236), Dim = Color.FromArgb(110, 110, 120), Accent = Color.FromArgb(255, 59, 74);

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Dim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Accent, Math.Max(1.5f, r.Height / 8f));
        e.Graphics.DrawLines(pen, new PointF[]
        {
            new(r.Left + r.Width * .2f, r.Top + r.Height * .52f),
            new(r.Left + r.Width * .42f, r.Top + r.Height * .74f),
            new(r.Left + r.Width * .8f, r.Top + r.Height * .3f),
        });
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(28, 28, 34), Hover = Color.FromArgb(48, 48, 58), Line = Color.FromArgb(58, 58, 68);
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}

/// <summary>Short start / stop beeps, generated so the exe doesn't carry sound files.</summary>
static class Sounds
{
    static readonly SoundPlayer start = Make(660, 880), stop = Make(880, 587);

    public static void Start() => Play(start);
    public static void Stop() => Play(stop);

    static void Play(SoundPlayer p)
    {
        try { p.Play(); } catch { }
    }

    static SoundPlayer Make(double f1, double f2)
    {
        const int rate = 44100;
        int beep = rate * 70 / 1000, gap = rate * 30 / 1000, n = beep * 2 + gap;
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                int j = i < beep ? i : i - beep - gap;
                double f = i < beep ? f1 : f2;
                double v = j < 0 ? 0 : Math.Sin(2 * Math.PI * f * j / rate) * Math.Sin(Math.PI * j / beep) * 0.25;
                w.Write((short)(v * short.MaxValue));
            }
        }
        ms.Position = 0;
        var p = new SoundPlayer(ms);
        p.Load();
        return p;
    }
}

static class Native
{
    public static void DarkTitleBar(IntPtr hwnd)
    {
        int dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
