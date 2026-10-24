using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace InstaClip;

record RecordOptions(Display Display, int Fps, int Height, string Quality, bool SystemAudio, bool Mic, string MicId, string Folder);

/// <summary>
/// One recording. <see cref="ScreenCapture"/> produces evenly timed frames and pipes them to FFmpeg, which encodes them
/// live at the chosen quality, mixes in the audio feeds and writes a .mkv (still playable if anything crashes).
/// Stopping remuxes it to .mp4 in a few seconds.
/// </summary>
sealed class Recording
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Dictionary<string, double> Bpp = new() { ["high"] = .15, ["good"] = .09, ["small"] = .045 };   // bits per pixel per frame, as in Clipper
    static int pipeCount;

    readonly string ffmpeg, folder, name, tempPath;
    readonly ScreenCapture capture;
    readonly List<AudioFeed> feeds;
    readonly StringBuilder err = new();
    readonly NamedPipeServerStream video;
    Process proc;

    public string Warning { get; private init; }
    public event Action Exited;
    public bool HasExited => proc.HasExited;

    Recording(string ffmpeg, string folder, string name, string tempPath, ScreenCapture capture, List<AudioFeed> feeds)
    {
        this.ffmpeg = ffmpeg;
        this.folder = folder;
        this.name = name;
        this.tempPath = tempPath;
        this.capture = capture;
        this.feeds = feeds;
        // raw frames are ~3 MB each (1080p): a big pipe buffer lets each one go across in a few large writes
        VideoPipe = $"instaclip-{Environment.ProcessId}-{Interlocked.Increment(ref pipeCount)}-video";
        video = new NamedPipeServerStream(VideoPipe, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 16 << 20);
    }

    readonly string VideoPipe;

    /// <param name="hwEncoder">h264_nvenc / h264_amf / h264_qsv, or null for x264 on the CPU.</param>
    public static async Task<Recording> Start(string ffmpeg, string hwEncoder, RecordOptions o)
    {
        try { File.Delete(LogPath); } catch { }
        Prioritize(Process.GetCurrentProcess());
        ScreenCapture capture;
        try { capture = await Task.Run(() => new ScreenCapture(o.Display, o.Height, o.Fps)); }
        catch (Exception ex) { throw new Exception("Couldn't capture the screen. " + ex.Message); }

        int w = capture.Width, h = capture.Height;
        int kbps = Math.Max(500, (int)(w * (double)h * o.Fps * Bpp.GetValueOrDefault(o.Quality ?? "good", .09) / 1000));
        var name = "InstaClip_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv);
        var temp = Unique(o.Folder, name, ".recording.mkv");

        string warning = null, error = "";
        foreach (var enc in hwEncoder == null ? [null] : new[] { hwEncoder, null })
        {
            // WASAPI objects are made off the UI thread so their callbacks run on their own threads
            var (feeds, warn) = await Task.Run(() => OpenFeeds(o));
            warning ??= warn;
            var r = new Recording(ffmpeg, o.Folder, name, temp, capture, feeds) { Warning = warning };
            try
            {
                if (await r.Launch(Args(o, w, h, kbps, enc, r.VideoPipe, feeds, temp))) return r;
                error = r.ErrorTail();
            }
            catch (Exception ex) { error = ex.Message; }
            await Task.Run(r.Abort);
        }
        capture.Dispose();
        throw new Exception("Couldn't start recording. " + error);
    }

    static (List<AudioFeed> Feeds, string Warning) OpenFeeds(RecordOptions o)
    {
        var feeds = new List<AudioFeed>();
        string warn = null;
        int n = Interlocked.Increment(ref pipeCount);
        if (o.SystemAudio)
            try { feeds.Add(AudioFeed.Speakers($"instaclip-{Environment.ProcessId}-{n}-sys")); }
            catch { warn = "System audio couldn't be captured, so the clip has no game sound."; }
        if (o.Mic)
            try { feeds.Add(AudioFeed.Mic(o.MicId, $"instaclip-{Environment.ProcessId}-{n}-mic")); }
            catch { warn = (warn == null ? "" : warn + " ") + "The microphone couldn't be opened, so it isn't in the clip."; }
        return (feeds, warn);
    }

    static List<string> Args(RecordOptions o, int w, int h, int kbps, string enc, string videoPipe, List<AudioFeed> feeds, string output)
    {
        // Video arrives as raw NV12 at an exact frame rate, already scaled, audio as raw PCM: all through named pipes
        var a = new List<string>
        {
            "-hide_banner", "-y", "-nostdin", "-nostats", "-progress", "pipe:1", "-stats_period", "0.25",
            "-thread_queue_size", "16", "-f", "rawvideo", "-pix_fmt", "nv12", "-video_size", $"{w}x{h}", "-framerate", o.Fps.ToString(Inv),
            "-i", @"\\.\pipe\" + videoPipe,
        };
        foreach (var f in feeds)
            a.AddRange(["-thread_queue_size", "4096", "-f", f.SampleFmt, "-ar", f.SampleRate.ToString(Inv), "-ch_layout", f.Layout, "-i", f.PipePath]);

        const string Stereo = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
        if (feeds.Count == 1) a.AddRange(["-filter_complex", $"[1:a]{Stereo}[a]"]);
        else if (feeds.Count == 2) a.AddRange(["-filter_complex", $"[1:a]{Stereo}[a1];[2:a]{Stereo}[a2];[a1][a2]amix=inputs=2:normalize=0[a]"]);
        a.AddRange(["-map", "0:v"]);
        if (feeds.Count > 0) a.AddRange(["-map", "[a]"]);

        a.AddRange(enc switch
        {
            "h264_nvenc" => ["-c:v", enc, "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-multipass", "qres", "-spatial-aq", "1", "-profile:v", "high"],
            "h264_amf" => ["-c:v", enc, "-quality", "balanced", "-rc", "vbr_peak"],
            "h264_qsv" => ["-c:v", enc, "-preset", "medium"],
            _ => (string[])["-c:v", "libx264", "-preset", "superfast"],
        });
        a.AddRange(["-b:v", kbps + "k", "-maxrate", kbps * 3 / 2 + "k", "-bufsize", kbps * 2 + "k", "-g", (o.Fps * 2).ToString(Inv)]);
        // what ScreenCapture's GPU conversion produces
        a.AddRange(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv"]);
        if (feeds.Count > 0) a.AddRange(["-c:a", "aac", "-b:a", "160k"]);
        a.AddRange(["-f", "matroska", output]);
        return a;
    }

    /// <summary>Starts FFmpeg, starts feeding it frames, and waits until it's encoding. False if it gave up instead.</summary>
    async Task<bool> Launch(List<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        lock (err) err.AppendLine("ffmpeg " + string.Join(" ", args.Select(x => x.Contains(' ') ? $"\"{x}\"" : x)));
        proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.OutputDataReceived += (_, e) =>
        {
            // -progress output reports "frame=N" while encoding
            if (e.Data != null && e.Data.StartsWith("frame=") && int.TryParse(e.Data.AsSpan(6), out var n) && n > 0)
                started.TrySetResult(true);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (err) { err.AppendLine(e.Data); if (err.Length > 32000) err.Remove(0, err.Length - 16000); }
        };
        proc.Exited += (_, _) => { started.TrySetResult(false); Exited?.Invoke(); };
        proc.Start();
        Prioritize(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var timeout = Task.Delay(15000);
        var connected = video.WaitForConnectionAsync();
        if (await Task.WhenAny(connected, started.Task, timeout) != connected || connected.IsFaulted) return false;
        // the audio starts at the exact moment of the first video frame
        capture.Start(video, t0 => { foreach (var f in feeds) f.Begin(t0); });
        return await Task.WhenAny(started.Task, timeout) == started.Task && started.Task.Result;
    }

    // Games load the CPU and GPU to the max. Without this the capture gets starved and drops frames: higher CPU priority,
    // higher GPU scheduling priority (what OBS does too), and no Windows 11 power throttling for a windowless process.
    static void Prioritize(Process p)
    {
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        try { D3DKMTSetProcessSchedulingPriorityClass(p.Handle, 4); } catch { }   // D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH
        try
        {
            // PROCESS_POWER_THROTTLING_EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION controlled, both turned off
            var state = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
            SetProcessInformation(p.Handle, 4, ref state, Marshal.SizeOf<PowerThrottlingState>());   // ProcessPowerThrottling
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    [DllImport("gdi32.dll")]
    static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

    string ErrorTail()
    {
        lock (err) return string.Join("\n", err.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
    }

    // %LOCALAPPDATA%\InstaClip\last-recording.log: the FFmpeg command and output of the latest recording, for troubleshooting
    static readonly string LogPath = Path.Combine(Settings.DataDir, "last-recording.log");

    void WriteLog()
    {
        try
        {
            lock (err)
            {
                if (capture.Error != null) err.AppendLine("capture: " + capture.Error.Message);
                File.AppendAllText(LogPath, err + Environment.NewLine);
            }
        }
        catch { }
    }

    // A failed attempt: the capture is kept for the next one
    void Abort()
    {
        try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
        capture.Stop();
        try { video.Dispose(); } catch { }
        foreach (var f in feeds) f.Dispose();
        try { proc?.WaitForExit(3000); } catch { }
        WriteLog();
        TryDelete(tempPath);
    }

    /// <summary>Stops capturing and ends the video and audio streams, which makes FFmpeg finish the file.</summary>
    public void EndCapture()
    {
        capture.Stop();
        try { video.Dispose(); } catch { }
        foreach (var f in feeds) f.Stop();
    }

    /// <summary>Waits for FFmpeg to close the .mkv, then remuxes it to .mp4. Returns the clip's path.</summary>
    public string Finish()
    {
        if (!proc.WaitForExit(15000))
        {
            try { proc.Kill(true); } catch { }
            proc.WaitForExit(5000);
        }
        foreach (var f in feeds) f.Dispose();
        WriteLog();
        capture.Dispose();
        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
        {
            TryDelete(tempPath);
            throw new Exception("FFmpeg didn't write anything. " + ErrorTail());
        }
        var mp4 = Unique(folder, name, ".mp4");
        if (Tools.Run(ffmpeg, "-hide_banner", "-v", "error", "-y", "-i", tempPath, "-map", "0", "-c", "copy", "-movflags", "+faststart", mp4) == 0 && File.Exists(mp4))
        {
            TryDelete(tempPath);
            return mp4;
        }
        TryDelete(mp4);
        // the .mkv plays fine too: keep it rather than lose the clip
        var mkv = Unique(folder, name, ".mkv");
        File.Move(tempPath, mkv);
        return mkv;
    }

    static string Unique(string dir, string name, string ext)
    {
        var p = Path.Combine(dir, name + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name}_{i}{ext}");
        return p;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

static class Tools
{
    public static string FindFfmpeg()
    {
        var bundled = ExtractBundledFfmpeg();
        var dirs = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "ffmpeg"), bundled }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Release builds embed FFmpeg (ffmpeg.zip) inside InstaClip.exe. It's unpacked once to %LOCALAPPDATA%\InstaClip
    /// and reused on later runs. Returns that folder, or null if this build doesn't carry FFmpeg.
    /// </summary>
    static string ExtractBundledFfmpeg()
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("ffmpeg.zip");
        if (res == null) return null;
        var dir = Path.Combine(Settings.DataDir, $"ffmpeg-{res.Length}");
        var ok = Path.Combine(dir, ".ok");
        if (File.Exists(ok)) return dir;
        var tmp = $"{dir}.tmp{Environment.ProcessId}";
        try
        {
            using (var zip = new ZipArchive(res)) zip.ExtractToDirectory(tmp, true);
            File.WriteAllText(Path.Combine(tmp, ".ok"), "");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
        }
        catch
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
        return File.Exists(ok) ? dir : null;
    }

    /// <summary>h264_nvenc / h264_amf / h264_qsv if this PC has a working one, else null (x264 on the CPU).</summary>
    public static string DetectHwEncoder(string ffmpeg)
    {
        if (ffmpeg == null) return null;
        foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
            if (Run(ffmpeg, "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.1", "-vf", "format=nv12",
                "-frames:v", "1", "-c:v", enc, "-f", "null", "-") == 0)
                return enc;
        return null;
    }

    public static int Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        var errTask = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        errTask.Wait();
        return p.ExitCode;
    }
}
