using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace InstaClip;

/// <summary>
/// Captures a monitor the way OBS does on Windows 10/11: Windows.Graphics.Capture hands over the screen as a GPU texture
/// whenever it changes, and a precise clock of our own (high-resolution timer, not Sleep) samples the newest one
/// exactly <c>fps</c> times a second. The GPU resizes it and converts it to NV12, and only that small frame is read back
/// and written to FFmpeg as raw video, so the timeline is perfectly even and FFmpeg just encodes.
/// </summary>
sealed class ScreenCapture : IDisposable
{
    const int Lag = 2;          // frames the GPU gets to finish a frame before we read it back (OBS does the same)
    const int Ring = Lag + 2;   // staging textures in flight

    public readonly int Width, Height, SourceWidth, SourceHeight;
    readonly int fps;
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11VideoDevice videoDevice;
    readonly ID3D11VideoContext videoContext;
    readonly IDirect3DDevice winrtDevice;
    readonly GraphicsCaptureItem item;
    readonly Direct3D11CaptureFramePool pool;
    readonly GraphicsCaptureSession session;
    readonly ID3D11Texture2D nv12;
    readonly ID3D11Texture2D[] staging = new ID3D11Texture2D[Ring];
    readonly int frameSize;   // bytes of one NV12 frame
    // these depend on the source size, which can change mid-recording (e.g. a game switches resolution)
    ID3D11Texture2D bgra;
    ID3D11VideoProcessorEnumerator enumerator;
    ID3D11VideoProcessor processor;
    ID3D11VideoProcessorInputView inputView;
    ID3D11VideoProcessorOutputView outputView;
    Windows.Graphics.SizeInt32 poolSize;
    Thread thread;
    volatile bool stopping, writerFailed;

    /// <summary>Why the capture loop ended on its own, if it did (null after a normal <see cref="Stop"/>).</summary>
    public Exception Error { get; private set; }

    /// <param name="height">Output height, 0 = the monitor's own.</param>
    public ScreenCapture(Display display, int height, int fps)
    {
        this.fps = fps;
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device, out context).CheckError();
            using (var mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);   // the capture API uses the device from its own threads
            using (var dxgi = device.QueryInterface<IDXGIDevice>())
            {
                dxgi.SetGPUThreadPriority(7);   // same as OBS: don't let a game starve our few GPU jobs
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
                winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                Marshal.Release(inspectable);
            }
            videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            videoContext = context.QueryInterface<ID3D11VideoContext>();

            item = CaptureItemForMonitor(display.Monitor);
            poolSize = item.Size;
            SourceWidth = poolSize.Width;
            SourceHeight = poolSize.Height;
            Height = height > 0 && height < SourceHeight ? height : SourceHeight;
            Width = Height == SourceHeight ? SourceWidth : (int)Math.Round(SourceWidth * (double)Height / SourceHeight / 2) * 2;
            Width -= Width % 2;
            Height -= Height % 2;
            frameSize = Width * Height * 3 / 2;

            nv12 = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)Width, (uint)Height, 1, 1, BindFlags.RenderTarget));
            for (int i = 0; i < Ring; i++)
                staging[i] = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)Width, (uint)Height, 1, 1, BindFlags.None,
                    ResourceUsage.Staging, CpuAccessFlags.Read));
            CreateProcessor(SourceWidth, SourceHeight);

            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
            session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = true; } catch { }
            try
            {
                // Windows 11: no yellow "being captured" border (Windows 10 always draws it)
                GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(2000);
                session.IsBorderRequired = false;
            }
            catch { }
            session.StartCapture();

            // the first frame comes right after starting; without it the recording would open on black
            var sw = Stopwatch.StartNew();
            while (!Grab() && sw.ElapsedMilliseconds < 1000) Thread.Sleep(5);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void CreateProcessor(int w, int h)
    {
        outputView?.Dispose();
        inputView?.Dispose();
        processor?.Dispose();
        enumerator?.Dispose();
        bgra?.Dispose();
        bgra = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        enumerator = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)w, InputHeight = (uint)h,
            OutputWidth = (uint)Width, OutputHeight = (uint)Height,
            InputFrameRate = new Rational((uint)fps, 1), OutputFrameRate = new Rational((uint)fps, 1),
            Usage = VideoUsage.OptimalSpeed,
        });
        processor = videoDevice.CreateVideoProcessor(enumerator, 0);
        // full-range RGB in, BT.709 limited-range YUV out (what players expect from H.264)
        if (videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>() is { } vc1)
        {
            using (vc1)
            {
                vc1.VideoProcessorSetStreamColorSpace1(processor, 0, ColorSpaceType.RgbFullG22NoneP709);
                vc1.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.YcbcrStudioG22LeftP709);
            }
        }
        videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
        inputView = videoDevice.CreateVideoProcessorInputView(bgra, enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        outputView = videoDevice.CreateVideoProcessorOutputView(nv12, enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
    }

    /// <summary>Copies the newest frame the capture API has (if it has a new one) into <see cref="bgra"/>.</summary>
    bool Grab()
    {
        Direct3D11CaptureFrame latest = null, f;
        while ((f = pool.TryGetNextFrame()) != null)
        {
            latest?.Dispose();
            latest = f;
        }
        if (latest == null) return false;
        using (latest)
        {
            var size = latest.ContentSize;
            if (size.Width != poolSize.Width || size.Height != poolSize.Height)
            {
                // the monitor changed resolution: frames from now on come at the new size, scaled to the same output
                poolSize = size;
                pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                CreateProcessor(size.Width, size.Height);
                return false;
            }
            using var tex = TextureOf(latest.Surface);
            context.CopyResource(bgra, tex);
        }
        return true;
    }

    // Converts + resizes the current screen into staging[slot], all on the GPU
    void Render(int slot)
    {
        videoContext.VideoProcessorBlt(processor, outputView, 0, 1,
            [new VideoProcessorStream { Enable = true, InputSurface = inputView }]);
        context.CopyResource(staging[slot], nv12);
    }

    // Reads staging[slot] back into `dest` (Y plane, then interleaved UV plane, no row padding)
    unsafe void Read(int slot, byte[] dest)
    {
        var map = context.Map(staging[slot], 0, MapMode.Read);
        try
        {
            byte* src = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch, rows = Height * 3 / 2;
            fixed (byte* dst = dest)
            {
                // the UV plane starts right after Height rows of the Y plane
                for (int y = 0; y < rows; y++)
                    Buffer.MemoryCopy(src + (long)y * pitch, dst + (long)y * Width, Width, Width);
            }
        }
        finally { context.Unmap(staging[slot], 0); }
    }

    /// <summary>
    /// Starts writing raw NV12 frames to <paramref name="sink"/> on a dedicated thread. <paramref name="onStart"/> gets the
    /// Stopwatch timestamp of frame 0; frame n is the screen at that moment + n/fps.
    /// </summary>
    public void Start(Stream sink, Action<long> onStart)
    {
        Stop();
        stopping = false;
        Error = null;
        thread = new Thread(() => Loop(sink, onStart)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "InstaClip capture" };
        thread.Start();
    }

    /// <summary>Stops the loop after it wrote out the frames still in flight.</summary>
    public void Stop()
    {
        stopping = true;
        thread?.Join();
        thread = null;
    }

    void Loop(Stream sink, Action<long> onStart)
    {
        timeBeginPeriod(1);
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);   // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
        if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
        // Frames go to FFmpeg from a separate thread through a short queue, so a slow write never delays a capture tick.
        // null in the queue = repeat the previous frame.
        var queue = new BlockingCollection<byte[]>(QueueFrames);
        var free = new ConcurrentBag<byte[]>();
        writerFailed = false;
        var writer = new Thread(() =>
        {
            byte[] prev = null;
            try
            {
                foreach (var buf in queue.GetConsumingEnumerable())
                {
                    if (buf != null)
                    {
                        if (prev != null) free.Add(prev);
                        prev = buf;
                    }
                    sink.Write(prev);
                }
            }
            catch (Exception ex)
            {
                Error ??= ex;   // usually FFmpeg went away
                writerFailed = true;
            }
        }) { IsBackground = true, Name = "InstaClip frame writer" };
        writer.Start();

        void Emit(int slot)
        {
            byte[] buf = null;
            if (slot >= 0)
            {
                if (!free.TryTake(out buf)) buf = new byte[frameSize];
                Read(slot, buf);
            }
            while (!queue.TryAdd(buf, 100))
                if (writerFailed) throw new IOException("FFmpeg stopped reading");
        }

        try
        {
            long freq = Stopwatch.Frequency;
            Grab();
            Render(0);
            Emit(0);
            long t0 = Stopwatch.GetTimestamp();
            onStart(t0);

            var pending = new Queue<int>();   // staging slots waiting to be read back; -1 = repeat the previous frame
            int slot = 1;
            for (long tick = 1; !stopping && !writerFailed;)
            {
                WaitUntil(timer, t0 + tick * freq / fps);
                // fell behind (system hiccup)? those moments can't be captured anymore: repeat the last frame
                // for them so the video stays on real time and in sync with the audio
                long late = (Stopwatch.GetTimestamp() - (t0 + tick * freq / fps)) * fps / freq;
                for (long i = 0; i < late; i++, tick++) pending.Enqueue(-1);

                Grab();
                Render(slot);
                pending.Enqueue(slot);
                slot = (slot + 1) % Ring;
                tick++;
                while (pending.Count > Lag) Emit(pending.Dequeue());
            }
            while (pending.Count > 0 && !writerFailed) Emit(pending.Dequeue());
        }
        catch (Exception ex) { Error ??= ex; }
        finally
        {
            queue.CompleteAdding();
            writer.Join();
            // end the video stream: FFmpeg then finishes the file (also when capture broke, so the clip is still saved)
            try { sink.Dispose(); } catch { }
            if (timer != IntPtr.Zero) CloseHandle(timer);
            timeEndPeriod(1);
        }
    }

    const int QueueFrames = 16;   // ~0.25 s at 60 fps: covers FFmpeg starting up and short stalls

    static void WaitUntil(IntPtr timer, long target)
    {
        long ticks100ns = (target - Stopwatch.GetTimestamp()) * 10_000_000 / Stopwatch.Frequency;
        if (ticks100ns <= 0) return;
        long due = -ticks100ns;   // negative = relative
        if (timer != IntPtr.Zero && SetWaitableTimerEx(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            WaitForSingleObject(timer, 0xFFFFFFFF);
        else
            Thread.Sleep((int)(ticks100ns / 10_000));
    }

    static GraphicsCaptureItem CaptureItemForMonitor(IntPtr monitor)
    {
        const string ClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");   // IGraphicsCaptureItemInterop
        var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");      // IGraphicsCaptureItem
        Marshal.ThrowExceptionForHR(WindowsCreateString(ClassName, ClassName.Length, out var name));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref interopIid, out var interop));
            try
            {
                IntPtr result;
                unsafe
                {
                    // vtable: IUnknown (3), CreateForWindow, CreateForMonitor
                    var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)interop)[4];
                    Marshal.ThrowExceptionForHR(fn(interop, monitor, &itemIid, &result));
                }
                var item = GraphicsCaptureItem.FromAbi(result);
                Marshal.Release(result);
                return item;
            }
            finally { Marshal.Release(interop); }
        }
        finally { WindowsDeleteString(name); }
    }

    static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var accessIid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");    // IDirect3DDxgiInterfaceAccess
        var textureIid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");   // ID3D11Texture2D
        var abi = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, ref accessIid, out var access));
        try
        {
            IntPtr tex;
            unsafe
            {
                // vtable: IUnknown (3), GetInterface
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
                Marshal.ThrowExceptionForHR(fn(access, &textureIid, &tex));
            }
            return new ID3D11Texture2D(tex);
        }
        finally { Marshal.Release(access); }
    }

    public void Dispose()
    {
        Stop();
        try { session?.Dispose(); } catch { }
        try { pool?.Dispose(); } catch { }
        try { (winrtDevice as IDisposable)?.Dispose(); } catch { }
        inputView?.Dispose();
        outputView?.Dispose();
        processor?.Dispose();
        enumerator?.Dispose();
        bgra?.Dispose();
        nv12?.Dispose();
        foreach (var s in staging) s?.Dispose();
        videoContext?.Dispose();
        videoDevice?.Dispose();
        context?.Dispose();
        device?.Dispose();
    }

    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(IntPtr className, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint period);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
