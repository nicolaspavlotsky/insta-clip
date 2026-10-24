using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace InstaClip;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, @"Local\InstaClip", out bool first);
        if (!first)
        {
            MessageBox.Show("InstaClip is already running. You'll find it in the system tray.", "InstaClip", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

enum State { Idle, Starting, Recording, Saving }

/// <summary>The whole app: a tray icon, a global shortcut that starts / stops a <see cref="Recording"/>, and the badge.</summary>
sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly Settings cfg;
    readonly ContextMenuStrip menu;
    readonly NotifyIcon tray;
    readonly HotkeyWindow hotkey = new();
    readonly Overlay overlay = new();
    readonly Icon idleIcon = LoadIcon("instaclip.ico"), recIcon = LoadIcon("recording.ico");
    readonly SynchronizationContext ui;
    readonly Task<(string Ffmpeg, string Encoder)> tools;
    State state;
    Recording rec;
    string lastClip, balloonFile;
    bool exiting;

    string Hk => Hotkey.Format(cfg.HotkeyMods, cfg.HotkeyKey);

    public TrayApp()
    {
        bool firstRun = !Settings.Exists;
        cfg = Settings.Load();
        if (firstRun) cfg.Save();
        ToolStripManager.Renderer = new DarkMenuRenderer();
        menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        // FFmpeg unpacking and the GPU encoder check take a second or two: do them now, not on the first shortcut press
        tools = Task.Run(() => { var f = Tools.FindFfmpeg(); return (f, Tools.DetectHwEncoder(f)); });

        tray = new NotifyIcon { Icon = idleIcon, ContextMenuStrip = menu, Visible = true };
        menu.Opening += (_, _) => BuildMenu();
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };
        tray.BalloonTipClicked += (_, _) => Reveal(balloonFile);
        hotkey.Pressed += Toggle;
        UpdateTray();
        RefreshStartupPath();

        if (!hotkey.Register(cfg.HotkeyMods, cfg.HotkeyKey))
            Notify($"{Hk} is already used by another app. Pick a different shortcut from the tray menu.", ToolTipIcon.Warning);
        else if (firstRun)
            Notify($"Press {Hk} to start recording, and {Hk} again to save the clip. Settings are in this tray icon's menu.", ToolTipIcon.Info);
    }

    async void Toggle()
    {
        if (exiting) return;
        if (state == State.Idle) await StartRecording();
        else if (state == State.Recording) await StopRecording();
        // while starting or saving, presses are ignored
    }

    async Task StartRecording()
    {
        var display = PickDisplay(Display.All());
        if (display == null) { Notify("Couldn't find a monitor to record.", ToolTipIcon.Error); return; }
        SetState(State.Starting);
        if (cfg.Sounds) Sounds.Start();
        overlay.ShowOn(display, cfg.Overlay, "Starting…");
        Recording r;
        try
        {
            var (ffmpeg, enc) = await tools;
            if (ffmpeg == null) throw new Exception("ffmpeg.exe not found. Put it next to InstaClip.exe.");
            Directory.CreateDirectory(cfg.Folder);
            r = await Recording.Start(ffmpeg, enc, new RecordOptions(display, cfg.Fps, cfg.Height, cfg.Quality, cfg.SystemAudio, cfg.Mic, cfg.MicId, cfg.Folder));
        }
        catch (Exception ex)
        {
            overlay.HideBadge();
            SetState(State.Idle);
            Notify(ex.Message, ToolTipIcon.Error);
            return;
        }
        rec = r;
        SetState(State.Recording);
        overlay.StartClock();
        // FFmpeg can die mid-recording (e.g. the monitor changes resolution): save what it got
        r.Exited += () => ui.Post(_ => OnUnexpectedExit(r), null);
        if (r.HasExited) OnUnexpectedExit(r);
    }

    void OnUnexpectedExit(Recording r)
    {
        if (rec == r && state == State.Recording) _ = StopRecording(unexpected: true);
    }

    async Task StopRecording(bool unexpected = false)
    {
        var r = rec;
        SetState(State.Saving);
        overlay.SetStatus("Saving…");
        try
        {
            await Task.Run(r.EndCapture);
            if (cfg.Sounds) Sounds.Stop();   // only now, so the beep isn't in the clip
            var path = await Task.Run(r.Finish);
            lastClip = path;
            var msg = $"{Path.GetFileName(path)} · {new FileInfo(path).Length / 1048576.0:0.0} MB";
            if (unexpected) msg = "The recording stopped by itself. Saved what was captured.\n" + msg;
            // shown now rather than at start, so the warning doesn't end up in the video
            if (r.Warning != null) msg += "\n" + r.Warning;
            Notify(msg, ToolTipIcon.Info, "Clip saved", path);
        }
        catch (Exception ex)
        {
            Notify("Couldn't save the clip. " + ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            rec = null;
            overlay.HideBadge();
            SetState(State.Idle);
        }
    }

    Display PickDisplay(List<Display> all) =>
        all.FirstOrDefault(d => d.DeviceName == cfg.Monitor) ?? all.FirstOrDefault(d => d.Primary) ?? all.FirstOrDefault();

    void SetState(State s)
    {
        state = s;
        UpdateTray();
    }

    void UpdateTray()
    {
        tray.Icon = state is State.Starting or State.Recording ? recIcon : idleIcon;
        var text = state switch
        {
            State.Starting => "InstaClip: starting…",
            State.Recording => $"InstaClip: recording. {Hk} to save",
            State.Saving => "InstaClip: saving the clip…",
            _ => $"InstaClip: {Hk} to record",
        };
        tray.Text = text.Length > 63 ? text[..63] : text;
    }

    void Notify(string text, ToolTipIcon icon, string title = "InstaClip", string file = null)
    {
        balloonFile = file;
        tray.ShowBalloonTip(5000, title, text.Length > 250 ? text[..250] : text, icon);
    }

    void ShowMenu() =>
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(tray, null);

    // ---------- menu ----------

    void BuildMenu()
    {
        menu.Items.Clear();
        var main = Item(state switch
        {
            State.Recording => "Stop and save clip",
            State.Starting => "Starting…",
            State.Saving => "Saving…",
            _ => "Start recording",
        }, Toggle, state is State.Idle or State.Recording);
        main.ShortcutKeyDisplayString = Hk;
        main.Font = new Font(menu.Font, FontStyle.Bold);

        var displays = Display.All();
        var current = PickDisplay(displays);
        var audio = new List<ToolStripItem>
        {
            Item("System audio", () => Set(() => cfg.SystemAudio = !cfg.SystemAudio), check: cfg.SystemAudio),
            Item("Microphone", () => Set(() => cfg.Mic = !cfg.Mic), check: cfg.Mic),
            new ToolStripSeparator(),
        };
        audio.AddRange(Choices(cfg.MicId, v => cfg.MicId = v, [("Default microphone", null), .. MicDevices()]));

        menu.Items.AddRange(new ToolStripItem[]
        {
            main,
            new ToolStripSeparator(),
            Item("Open clips folder", OpenFolder),
            Item("Copy last clip", CopyLastClip, File.Exists(lastClip)),
            new ToolStripSeparator(),
            Item($"Save clips to: {ShortPath(cfg.Folder)}…", PickFolder),
            Sub("Quality", Choices(cfg.Quality, v => cfg.Quality = v, ("High", "high"), ("Good (recommended)", "good"), ("Small", "small"))),
            Sub("Resolution", Choices(cfg.Height, v => cfg.Height = v, ("Same as monitor", 0), ("1440p", 1440), ("1080p", 1080), ("720p", 720))),
            Sub("Frame rate", Choices(cfg.Fps, v => cfg.Fps = v, ("60 fps", 60), ("30 fps", 30))),
            Sub("Monitor", displays.Count == 0
                ? [Item("No monitors found", null, false)]
                : Choices(current?.DeviceName, v => cfg.Monitor = v, displays.Select(d => (d.Label, d.DeviceName)).ToArray())),
            Sub("Audio", [.. audio]),
            Sub("Recording indicator", Choices(cfg.Overlay, v => cfg.Overlay = v,
                ("Top right", "top-right"), ("Top left", "top-left"), ("Bottom right", "bottom-right"), ("Bottom left", "bottom-left"), ("Hidden", "off"))),
            Item($"Shortcut: {Hk}…", ChangeHotkey, state == State.Idle),
            Item("Sounds", () => Set(() => cfg.Sounds = !cfg.Sounds), check: cfg.Sounds),
            Item("Start with Windows", ToggleStartup, check: StartupEnabled()),
            new ToolStripSeparator(),
            Item("Exit", Exit),
        });
    }

    static ToolStripMenuItem Item(string text, Action click, bool enabled = true, bool check = false)
    {
        var i = new ToolStripMenuItem(text) { Enabled = enabled, Checked = check };
        i.Click += (_, _) => click?.Invoke();
        return i;
    }

    static ToolStripMenuItem Sub(string text, params ToolStripItem[] items)
    {
        var s = new ToolStripMenuItem(text);
        s.DropDownItems.AddRange(items);
        if (s.DropDown is ToolStripDropDownMenu dd) { dd.ShowImageMargin = false; dd.ShowCheckMargin = true; }
        return s;
    }

    ToolStripItem[] Choices<T>(T current, Action<T> set, params (string Label, T Value)[] options) =>
        options.Select(o => (ToolStripItem)Item(o.Label, () => Set(() => set(o.Value)), check: EqualityComparer<T>.Default.Equals(o.Value, current))).ToArray();

    void Set(Action change)
    {
        change();
        cfg.Save();
    }

    static (string, string)[] MicDevices()
    {
        try
        {
            return new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => (d.FriendlyName, d.ID)).ToArray();
        }
        catch { return []; }
    }

    static string ShortPath(string path)
    {
        if (path.Length <= 40) return path;
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 3 ? $"{parts[0]}\\…\\{parts[^2]}\\{parts[^1]}" : path;
    }

    void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(cfg.Folder);
            Process.Start("explorer.exe", $"\"{cfg.Folder}\"");
        }
        catch (Exception ex) { Notify(ex.Message, ToolTipIcon.Error); }
    }

    static void Reveal(string file)
    {
        if (File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
    }

    void CopyLastClip()
    {
        if (File.Exists(lastClip)) Clipboard.SetFileDropList(new StringCollection { lastClip });
    }

    void PickFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Where should clips be saved?", UseDescriptionForTitle = true, InitialDirectory = cfg.Folder };
        if (d.ShowDialog() == DialogResult.OK) Set(() => cfg.Folder = d.SelectedPath);
    }

    void ChangeHotkey()
    {
        hotkey.Unregister();   // otherwise pressing the current shortcut would start recording instead of reaching the box
        using var d = new HotkeyDialog();
        if (d.ShowDialog() == DialogResult.OK && !(d.Mods == cfg.HotkeyMods && d.Key == cfg.HotkeyKey))
        {
            if (hotkey.Register(d.Mods, d.Key))
            {
                Set(() => { cfg.HotkeyMods = d.Mods; cfg.HotkeyKey = d.Key; });
                UpdateTray();
                return;
            }
            MessageBox.Show($"{Hotkey.Format(d.Mods, d.Key)} is already used by another app. Keeping {Hk}.", "InstaClip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (!hotkey.Register(cfg.HotkeyMods, cfg.HotkeyKey))
            Notify($"{Hk} is already used by another app. Pick a different shortcut.", ToolTipIcon.Warning);
    }

    static bool StartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("InstaClip") != null;
    }

    static void ToggleStartup()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (k.GetValue("InstaClip") != null) k.DeleteValue("InstaClip", false);
        else k.SetValue("InstaClip", $"\"{Environment.ProcessPath}\"");
    }

    // If the exe was moved since "Start with Windows" was turned on, point the entry at the new location
    static void RefreshStartupPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var want = $"\"{Environment.ProcessPath}\"";
            if (k?.GetValue("InstaClip") is string have && have != want) k.SetValue("InstaClip", want);
        }
        catch { }
    }

    async void Exit()
    {
        if (exiting) return;
        exiting = true;
        while (state is State.Starting or State.Saving) await Task.Delay(100);
        if (state == State.Recording) await StopRecording();
        hotkey.Unregister();
        hotkey.DestroyHandle();
        tray.Visible = false;
        tray.Dispose();
        overlay.Dispose();
        ExitThread();
    }

    static Icon LoadIcon(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return new Icon(s, SystemInformation.SmallIconSize);
    }
}
