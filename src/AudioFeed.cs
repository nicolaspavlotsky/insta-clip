using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Wave;

namespace InstaClip;

/// <summary>
/// Captures one WASAPI stream (what the speakers play, via loopback, or a microphone) and feeds it to FFmpeg
/// as raw PCM through a named pipe.
/// The stream is locked to the wall clock from the moment the video's first frame was grabbed: audio from before
/// that is dropped and gaps become silence (loopback delivers nothing at all while nothing is playing), so it stays
/// in sync with the video however long the recording runs.
/// </summary>
sealed class AudioFeed : IDisposable
{
    static readonly long Freq = Stopwatch.Frequency;

    readonly IWaveIn capture;
    readonly NamedPipeServerStream pipe;
    readonly Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource cts = new();
    readonly Task writer;
    readonly System.Threading.Timer watchdog;
    readonly object gate = new();
    readonly List<(long At, byte[] Data)> early = [];   // audio captured before the video started, guarded by gate
    readonly int bytesPerSec, align;
    long start;     // Stopwatch timestamp of the video's first frame; 0 until known
    long written;   // bytes queued for FFmpeg since start
    bool stopped;

    public readonly string PipePath, SampleFmt, Layout;
    public readonly int SampleRate;

    public static AudioFeed Speakers(string pipeName) => new(new WasapiLoopbackCapture(), pipeName);

    public static AudioFeed Mic(string deviceId, string pipeName)
    {
        var devices = new MMDeviceEnumerator();
        MMDevice dev = null;
        if (deviceId != null)
            try { dev = devices.GetDevice(deviceId); if (dev.State != DeviceState.Active) dev = null; }
            catch { dev = null; }
        dev ??= devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        return new(new WasapiCapture(dev), pipeName);
    }

    AudioFeed(IWaveIn capture, string pipeName)
    {
        this.capture = capture;
        var wf = capture.WaveFormat;
        SampleFmt = SampleFormat(wf);
        if (SampleFmt == null) { capture.Dispose(); throw new NotSupportedException($"Unsupported audio format: {wf}"); }
        SampleRate = wf.SampleRate;
        Layout = wf.Channels switch { 1 => "mono", 2 => "stereo", 4 => "quad", 6 => "5.1", 8 => "7.1", var n => $"{n}c" };
        bytesPerSec = wf.AverageBytesPerSecond;
        align = wf.BlockAlign;
        PipePath = @"\\.\pipe\" + pipeName;
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        capture.DataAvailable += OnData;
        writer = Task.Run(Pump);
        watchdog = new System.Threading.Timer(_ => Tick(), null, 100, 100);
        try { capture.StartRecording(); }
        catch { Dispose(); throw; }
    }

    static string SampleFormat(WaveFormat wf)
    {
        var enc = wf.Encoding;
        if (wf is WaveFormatExtensible ext)
            enc = ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ? WaveFormatEncoding.IeeeFloat
                : ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM ? WaveFormatEncoding.Pcm : enc;
        return (enc, wf.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => "f32le",
            (WaveFormatEncoding.Pcm, 16) => "s16le",
            (WaveFormatEncoding.Pcm, 24) => "s24le",
            (WaveFormatEncoding.Pcm, 32) => "s32le",
            _ => null,
        };
    }

    void OnData(object sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        long now = Stopwatch.GetTimestamp();
        var data = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        lock (gate)
        {
            if (stopped) return;
            if (start == 0)
            {
                early.Add((now, data));
                early.RemoveAll(c => c.At < now - 3 * Freq);
                return;
            }
            Push(now, data);
        }
    }

    /// <summary>The video's first frame was grabbed at <paramref name="at"/> (a Stopwatch timestamp): the audio starts there too.</summary>
    public void Begin(long at)
    {
        lock (gate)
        {
            if (start != 0 || stopped) return;
            start = at;
            foreach (var (t, d) in early) Push(t, d);
            early.Clear();
        }
    }

    long Bytes(long ticks) => ticks * bytesPerSec / Freq / align * align;

    // Places a chunk that finished arriving at `at` on the timeline: pads with silence if the device skipped time,
    // trims if silence was already queued for it. Small jitter (under the slack) is left alone so the sound stays continuous.
    void Push(long at, byte[] data)
    {
        long end = Bytes(at - start), begin = end - data.Length, slack = Bytes(Freq * 6 / 100);
        if (end <= written) return;   // all of it is from before the video started, or already covered
        int skip = 0;
        if (begin > written + slack) Silence(begin - written);
        else if (begin < written - slack) skip = (int)(written - begin);
        Enqueue(skip == 0 ? data : data[skip..]);
    }

    // Loopback goes quiet while nothing is playing: keep the stream moving with silence
    void Tick()
    {
        lock (gate)
        {
            if (start == 0 || stopped) return;
            long due = Bytes(Stopwatch.GetTimestamp() - start - Freq / 5);   // the device gets 200 ms to deliver before we fill in
            if (due > written) Silence(due - written);
        }
    }

    void Silence(long bytes)
    {
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, 1 << 18);
            n -= n % align;
            if (n <= 0) return;
            Enqueue(new byte[n]);
            bytes -= n;
        }
    }

    void Enqueue(byte[] data)
    {
        written += data.Length;
        queue.Writer.TryWrite(data);
    }

    async Task Pump()
    {
        try
        {
            await pipe.WaitForConnectionAsync(cts.Token);
            Begin(Stopwatch.GetTimestamp());   // fallback in case FFmpeg's "first frame" line was never seen
            await foreach (var data in queue.Reader.ReadAllAsync(cts.Token))
                await pipe.WriteAsync(data, cts.Token);
        }
        catch { }   // FFmpeg went away, or we were stopped
    }

    /// <summary>Stops capturing and closes the pipe, which FFmpeg sees as the end of this audio input.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
        }
        watchdog.Dispose();
        try { capture.StopRecording(); } catch { }
        queue.Writer.TryComplete();
        if (!writer.Wait(500)) cts.Cancel();
        try { pipe.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        cts.Cancel();
        try { capture.Dispose(); } catch { }
    }
}
