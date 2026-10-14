using System.Runtime.InteropServices;

namespace LcdHub;

/// <summary>
/// Plays a video file on the LCD, cropped to fill, looping, at the file's own pace. Decodes
/// with Media Foundation's source reader, so it plays whatever Windows can (H.264, HEVC with
/// its extension, VP9, …) with no bundled codecs — on the GPU where there is one, else in
/// software. Frames that fall behind are skipped rather than slowing playback. Ends only on
/// failure, with <see cref="LcdFeed.Error"/> set.
/// </summary>
internal sealed unsafe class VideoFeed : LcdFeed
{
    private readonly string _path;

    public VideoFeed(string path, LcdPanel panel, Action<LcdFeed, uint[]> sink, Action ended)
        : base(panel, sink, ended) => _path = path;

    protected override void Run()
    {
        Mf.CoInitializeEx(IntPtr.Zero, Mf.COINIT_MULTITHREADED);
        var started = Mf.MFStartup(Mf.MF_VERSION, 0) >= 0;
        try
        {
            if (!started) Error = "Media Foundation is unavailable";
            else Play();
        }
        finally
        {
            if (started) Mf.MFShutdown();
            Mf.CoUninitialize();
        }
    }

    private void Play()
    {
        IntPtr manager = IntPtr.Zero, reader = IntPtr.Zero;
        try
        {
            manager = CreateDeviceManager();
            var (hwReader, hwError) = manager != IntPtr.Zero ? OpenReader(manager) : (IntPtr.Zero, null);
            reader = hwReader;
            if (reader == IntPtr.Zero)
            {
                if (hwError is not null) Diagnose.Log($"video: GPU path failed ({hwError}), decoding in software");
                (reader, var error) = OpenReader(IntPtr.Zero);
                if (reader == IntPtr.Zero) { Error = error; return; }
            }

            var (width, height, stride) = CurrentFormat(reader);
            Diagnose.Log($"video: {_path} {width}x{height} stride={stride} {(hwReader != IntPtr.Zero ? "gpu" : "software")}");
            using var canvas = new LcdCanvas(Width, Height);
            if (!canvas.IsValid) { Error = "no drawing surface"; return; }

            // Playback position is the feed's pause-aware clock minus where this loop began;
            // sample times are relative to the first sample of the loop.
            var loopStart = Clock;
            long? firstPts = null;
            int shown = 0, late = 0, busy = 0;
            double composeMs = 0;
            var statsAt = Clock;
            while (WaitWhilePaused())
            {
                var hr = Mf.ReadSample(reader, Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out var flags, out var pts, out var sample);
                if (hr < 0 || (flags & Mf.MF_SOURCE_READERF_ERROR) != 0) { Error = $"decode failed ({hr:X8})"; return; }
                if ((flags & Mf.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                    (width, height, stride) = CurrentFormat(reader);
                if ((flags & Mf.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    if (sample != IntPtr.Zero) Mf.Release(sample);
                    if (Mf.SeekToStart(reader) < 0) { Error = "can't loop"; return; }
                    loopStart = Clock;
                    firstPts = null;
                    continue;
                }
                if (sample == IntPtr.Zero) continue;
                try
                {
                    firstPts ??= pts;
                    var due = loopStart + TimeSpan.FromTicks(pts - firstPts.Value);
                    var early = due - Clock;
                    // More than two frames late: drop it and let the decoder catch up.
                    if (early < TimeSpan.FromMilliseconds(-66)) { late++; continue; }
                    if (!Sleep(early)) return;
                    // Decoded regardless, since later frames build on this one; just not scaled.
                    if (PanelBusy) { busy++; continue; }
                    var composeStart = Clock;
                    if (Compose(canvas, sample, width, height, stride) is { } frame)
                    {
                        composeMs += (Clock - composeStart).TotalMilliseconds;
                        Emit(frame);
                        shown++;
                    }
                }
                finally { Mf.Release(sample); }

                if (Diagnose.IsEnabled && Clock - statsAt >= TimeSpan.FromSeconds(5))
                {
                    Diagnose.Log($"video: {shown / (Clock - statsAt).TotalSeconds:0.0} fps shown, {late} late, " +
                                 $"{busy} while the panel was busy, scale {composeMs / Math.Max(1, shown):0.0} ms/frame");
                    (shown, late, busy, composeMs, statsAt) = (0, 0, 0, 0, Clock);
                }
            }
        }
        finally
        {
            if (reader != IntPtr.Zero) Mf.Release(reader);
            if (manager != IntPtr.Zero) Mf.Release(manager);
        }
    }

    /// <summary>
    /// A D3D11 device, wrapped for Media Foundation, so that decoding and the conversion to
    /// RGB32 run on the GPU and only the frames the panel takes are copied back. Zero where
    /// there's no GPU to be had, a remote session say.
    /// </summary>
    private static IntPtr CreateDeviceManager()
    {
        if (Mf.D3D11CreateDevice(IntPtr.Zero, Mf.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                Mf.D3D11_CREATE_DEVICE_VIDEO_SUPPORT | Mf.D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0,
                Mf.D3D11_SDK_VERSION, out var device, out _, out var context) < 0)
            return IntPtr.Zero;
        Mf.Release(context);
        try
        {
            // The decoder and video processor use the device from threads of their own.
            if (Mf.QueryInterface(device, Mf.IID_ID3D10Multithread, out var multithread) >= 0)
            {
                Mf.SetMultithreadProtected(multithread, true);
                Mf.Release(multithread);
            }
            if (Mf.MFCreateDXGIDeviceManager(out var token, out var manager) < 0) return IntPtr.Zero;
            if (Mf.ResetDevice(manager, device, token) >= 0) return manager;
            Mf.Release(manager);
            return IntPtr.Zero;
        }
        finally { Mf.Release(device); } // the manager holds its own reference
    }

    /// <summary>A source reader delivering the first video stream as RGB32, on the GPU when given its device manager.</summary>
    private (IntPtr Reader, string? Error) OpenReader(IntPtr manager)
    {
        IntPtr attrs = IntPtr.Zero, reader = IntPtr.Zero, type = IntPtr.Zero;
        try
        {
            if (Mf.MFCreateAttributes(out attrs, 3) < 0) return (IntPtr.Zero, "MFCreateAttributes failed");
            if (manager != IntPtr.Zero)
            {
                Mf.SetUnknown(attrs, Mf.MF_SOURCE_READER_D3D_MANAGER, manager);
                Mf.SetUINT32(attrs, Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                Mf.SetUINT32(attrs, Mf.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
            }
            // Either way the reader converts whatever the decoder emits to RGB32.
            else Mf.SetUINT32(attrs, Mf.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
            var hr = Mf.MFCreateSourceReaderFromURL(_path, attrs, out reader);
            if (hr < 0) return (IntPtr.Zero, $"can't open ({hr:X8})");

            Mf.SetStreamSelection(reader, Mf.MF_SOURCE_READER_ALL_STREAMS, false);
            Mf.SetStreamSelection(reader, Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);
            if (Mf.MFCreateMediaType(out type) < 0) return (IntPtr.Zero, "MFCreateMediaType failed");
            Mf.SetGUID(type, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            Mf.SetGUID(type, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_RGB32);
            hr = Mf.SetCurrentMediaType(reader, Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, type);
            if (hr < 0) return (IntPtr.Zero, $"no decodable video stream ({hr:X8})");

            (var result, reader) = (reader, IntPtr.Zero);
            return (result, null);
        }
        finally
        {
            if (type != IntPtr.Zero) Mf.Release(type);
            if (reader != IntPtr.Zero) Mf.Release(reader);
            if (attrs != IntPtr.Zero) Mf.Release(attrs);
        }
    }

    private static (int Width, int Height, int Stride) CurrentFormat(IntPtr reader)
    {
        IntPtr type = IntPtr.Zero;
        try
        {
            if (Mf.GetCurrentMediaType(reader, Mf.MF_SOURCE_READER_FIRST_VIDEO_STREAM, out type) < 0) return (0, 0, 0);
            Mf.GetUINT64(type, Mf.MF_MT_FRAME_SIZE, out var size);
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
            // Stored as a UINT32 but meant as a signed LONG; missing means packed and top-down.
            var stride = Mf.GetUINT32(type, Mf.MF_MT_DEFAULT_STRIDE, out var s) >= 0 ? (int)s : w * 4;
            return (w, h, stride);
        }
        finally { if (type != IntPtr.Zero) Mf.Release(type); }
    }

    private static uint[]? Compose(LcdCanvas canvas, IntPtr sample, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0) return null;
        if (Mf.ConvertToContiguousBuffer(sample, out var buffer) < 0) return null;
        try
        {
            // A 2-D buffer knows its real layout; fall back to the media type's stride otherwise.
            if (Mf.QueryInterface(buffer, Mf.IID_IMF2DBuffer, out var buffer2d) >= 0)
            {
                try
                {
                    if (Mf.Lock2D(buffer2d, out var scan0, out var pitch) < 0) return null;
                    try { return canvas.DrawFrom(scan0, width, height, pitch, fill: true); }
                    finally { Mf.Unlock2D(buffer2d); }
                }
                finally { Mf.Release(buffer2d); }
            }
            if (Mf.Lock(buffer, out var data) < 0) return null;
            try
            {
                var scan0 = stride < 0 ? data - stride * (height - 1) : data;
                return canvas.DrawFrom(scan0, width, height, stride, fill: true);
            }
            finally { Mf.Unlock(buffer); }
        }
        finally { Mf.Release(buffer); }
    }
}

/// <summary>
/// The slice of Media Foundation, and of the D3D11 it decodes on, that <see cref="VideoFeed"/>
/// uses: flat exports plus COM calls made straight through the vtable (slot numbers from
/// mfobjects.h / mfreadwrite.h / d3d10.h), which keeps it AOT-safe without COM interop.
/// </summary>
internal static unsafe partial class Mf
{
    public const uint MF_VERSION = 0x00020070;
    public const uint COINIT_MULTITHREADED = 0;
    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    public const uint MF_SOURCE_READERF_ERROR = 0x1;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    public const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x20;
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;
    public const uint D3D11_SDK_VERSION = 7;

    public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    public static readonly Guid IID_IMF2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("mfplat.dll")]
    public static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    public static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(out IntPtr mediaType);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr manager);

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    // IUnknown
    public static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
    {
        IntPtr p;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(obj, 0))(obj, &iid, &p);
        result = hr >= 0 ? p : IntPtr.Zero;
        return hr;
    }

    public static void Release(IntPtr obj) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);

    // IMFAttributes (IMFMediaType derives from it)
    public static int GetUINT32(IntPtr attrs, Guid key, out uint value)
    {
        uint v;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(attrs, 7))(attrs, &key, &v);
        value = v;
        return hr;
    }

    public static int GetUINT64(IntPtr attrs, Guid key, out ulong value)
    {
        ulong v;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(attrs, 8))(attrs, &key, &v);
        value = v;
        return hr;
    }

    public static int SetUINT32(IntPtr attrs, Guid key, uint value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attrs, 21))(attrs, &key, value);

    public static int SetGUID(IntPtr attrs, Guid key, Guid value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attrs, 24))(attrs, &key, &value);

    public static int SetUnknown(IntPtr attrs, Guid key, IntPtr value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)Slot(attrs, 27))(attrs, &key, value);

    // IMFDXGIDeviceManager
    public static int ResetDevice(IntPtr manager, IntPtr device, uint resetToken) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Slot(manager, 7))(manager, device, resetToken);

    // ID3D10Multithread
    public static void SetMultithreadProtected(IntPtr multithread, bool on) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(multithread, 5))(multithread, on ? 1 : 0);

    // IMFSourceReader
    public static int SetStreamSelection(IntPtr reader, uint stream, bool selected) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(reader, 4))(reader, stream, selected ? 1 : 0);

    public static int GetCurrentMediaType(IntPtr reader, uint stream, out IntPtr type)
    {
        IntPtr t;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(reader, 6))(reader, stream, &t);
        type = hr >= 0 ? t : IntPtr.Zero;
        return hr;
    }

    public static int SetCurrentMediaType(IntPtr reader, uint stream, IntPtr type) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Slot(reader, 7))(reader, stream, null, type);

    public static int SeekToStart(IntPtr reader)
    {
        // PROPVARIANT (24 bytes on x64): VT_I8 = 20 at offset 0, the 100 ns position at offset 8.
        var pv = stackalloc byte[24];
        new Span<byte>(pv, 24).Clear();
        *(ushort*)pv = 20;
        var timeFormat = Guid.Empty;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, int>)Slot(reader, 8))(reader, &timeFormat, pv);
    }

    public static int ReadSample(IntPtr reader, uint stream, out uint flags, out long timestamp, out IntPtr sample)
    {
        uint actual, f;
        long ts;
        IntPtr s;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(reader, 9))(
            reader, stream, 0, &actual, &f, &ts, &s);
        flags = f;
        timestamp = ts;
        sample = hr >= 0 ? s : IntPtr.Zero;
        return hr;
    }

    // IMFSample
    public static int ConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
    {
        IntPtr b;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &b);
        buffer = hr >= 0 ? b : IntPtr.Zero;
        return hr;
    }

    // IMFMediaBuffer
    public static int Lock(IntPtr buffer, out IntPtr data)
    {
        byte* p;
        uint max, cur;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &p, &max, &cur);
        data = (IntPtr)p;
        return hr;
    }

    public static int Unlock(IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);

    // IMF2DBuffer
    public static int Lock2D(IntPtr buffer, out IntPtr scan0, out int pitch)
    {
        byte* p;
        int pt;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte**, int*, int>)Slot(buffer, 3))(buffer, &p, &pt);
        scan0 = (IntPtr)p;
        pitch = pt;
        return hr;
    }

    public static int Unlock2D(IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);
}
