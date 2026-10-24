using System.Text.Json;

namespace InstaClip;

/// <summary>Everything set from the tray menu, kept in %LOCALAPPDATA%\InstaClip\settings.json.</summary>
sealed class Settings
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstaClip");
    static readonly string FilePath = Path.Combine(DataDir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "InstaClip");
    public string Quality { get; set; } = "good";       // high / good / small: same bits per pixel as Clipper's exports
    public int Height { get; set; }                      // 0 = the monitor's own resolution
    public int Fps { get; set; } = 60;
    public string Monitor { get; set; }                  // DXGI device name (\\.\DISPLAY1); null = main monitor
    public bool SystemAudio { get; set; } = true;
    public bool Mic { get; set; }
    public string MicId { get; set; }                    // null = Windows' default microphone
    public string Overlay { get; set; } = "top-right";   // top-right / top-left / bottom-right / bottom-left / off
    public bool Sounds { get; set; } = true;
    public int HotkeyMods { get; set; } = Hotkey.Alt;
    public int HotkeyKey { get; set; } = (int)Keys.A;

    public static bool Exists => File.Exists(FilePath);

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
