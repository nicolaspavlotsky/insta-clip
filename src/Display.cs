using System.Runtime.InteropServices;

namespace InstaClip;

/// <summary>A monitor, with the HMONITOR that Windows.Graphics.Capture records from.</summary>
record Display(int Number, string DeviceName, Rectangle Bounds, IntPtr Monitor, bool Primary)
{
    public string Label => $"{Number}: {Bounds.Width}×{Bounds.Height}" + (Primary ? " (main)" : "");

    public static List<Display> All() =>
        Screen.AllScreens.Select((s, i) =>
        {
            var center = new Point(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2);
            return new Display(i + 1, s.DeviceName, s.Bounds, MonitorFromPoint(center, 2), s.Primary);   // MONITOR_DEFAULTTONEAREST
        }).ToList();

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point pt, uint flags);
}
