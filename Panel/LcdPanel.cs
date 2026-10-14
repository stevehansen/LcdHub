using System.Runtime.InteropServices;

namespace LcdHub;

internal enum LcdWire { Hid, Ly }

/// <summary>
/// A supported panel that is plugged in. <see cref="Id"/> survives replugs and restarts, so
/// it keys what the panel was last told to show; <see cref="Path"/> is this connection.
/// </summary>
internal sealed record LcdDevice(string Path, string Id, int Width, int Height, LcdWire Wire);

/// <summary>
/// Drives one Thermalright USB LCD. Callers hand it finished top-down BGRX frames of
/// <see cref="Width"/>×<see cref="Height"/>; everything about the device — its wire, the
/// handshake, which way up it is mounted, JPEG encoding, reconnecting, and the keepalive —
/// stays in here. Protocols per github.com/Lexonight1/thermalright-trcc-linux:
/// <list type="bullet">
/// <item>0416:5302 Trofeo Vision LCD, 1280×480, HID reports ("type 2", hid_lcd.py).</item>
/// <item>0416:5408 Trofeo Vision 9.16", 1920×462, USB bulk through the WinUSB driver Windows
/// binds from the panel's own descriptors ("LY", ly_lcd.py).</item>
/// </list>
/// Both firmwares drop back to their own screen ~2–3 s after the last frame, so a background
/// thread resends the latest frame every second whether or not it changed.
/// </summary>
internal sealed class LcdPanel : IDisposable
{
    private const int ResendMs = 1000;
    private const int ReconnectMs = 5000;
    private const long JpegQuality = 90;
    // Busy video at q90 runs ~200 KB a frame, which the 1280×480's ~3.6 MB/s link caps near
    // 17 fps; q70 roughly halves that. Its firmware tops out at ~56 fps however small the
    // frame, the 9.16"'s at ~24.
    private const long MotionJpegQuality = 70;
    // TRCC never sends a JPEG this big, and the 9.16" silently keeps its previous picture
    // when given one.
    private const int MaxJpegBytes = 450_000;

    // The 9.16"'s device interface, declared in its MS OS 2.0 descriptors.
    private static readonly Guid LyInterface = new("3876E417-E089-4BCC-BBF8-47ABA55E46FB");

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly Thread _thread;
    private uint[]? _pendingPixels;
    private bool _pendingMotion;

    /// <summary>Starts driving the device; the first frame goes out once it answers.</summary>
    public LcdPanel(LcdDevice device)
    {
        Device = device;
        _thread = new Thread(Run) { IsBackground = true, Name = $"LcdPanel {device.Id}" };
        _thread.Start();
    }

    public LcdDevice Device { get; }
    public int Width => Device.Width;
    public int Height => Device.Height;

    /// <summary>Supported panels plugged in right now. Cheap; fine to call every second.</summary>
    public static List<LcdDevice> FindDevices()
    {
        var found = new List<LcdDevice>();
        LcdNative.HidD_GetHidGuid(out var hid);
        foreach (var path in InterfacePaths(hid))
            if (path.Contains("vid_0416&pid_5302", StringComparison.OrdinalIgnoreCase))
                found.Add(new LcdDevice(path, "0416:5302", 1280, 480, LcdWire.Hid));
        foreach (var path in InterfacePaths(LyInterface))
        {
            // \\?\USB#VID_0416&PID_5408#<serial>#{interface}
            var parts = path.Split('#');
            if (parts.Length > 2 && parts[1].Equals("vid_0416&pid_5408", StringComparison.OrdinalIgnoreCase))
                found.Add(new LcdDevice(path, "0416:5408:" + parts[2], 1920, 462, LcdWire.Ly));
        }
        return found;
    }

    /// <summary>
    /// Queues a Width×Height top-down BGRX frame. Takes ownership of the array. Only the newest
    /// frame is kept, so a caller outrunning the panel just loses frames. <paramref name="motion"/>
    /// marks one of a moving sequence, which trades sharpness for frame rate.
    /// </summary>
    public void Present(uint[] pixels, bool motion = false)
    {
        if (pixels.Length != Width * Height) throw new ArgumentException("frame size", nameof(pixels));
        lock (_gate)
        {
            _pendingPixels = pixels;
            _pendingMotion = motion;
        }
        _wake.Set();
    }

    /// <summary>
    /// A frame is queued that the panel hasn't taken yet, so one presented now would replace
    /// it unseen. Lets a producer skip the work of making frames faster than the panel shows them.
    /// </summary>
    public bool HasPendingFrame
    {
        get { lock (_gate) return _pendingPixels is not null; }
    }

    /// <summary>Stops sending; the panel reverts to its own screen a few seconds later.</summary>
    public void Dispose()
    {
        _stopped.Set();
        _wake.Set();
        _thread.Join(2000);
    }

    private void Run()
    {
        Link? link = null;
        byte[]? frame = null;
        long lastDumpMs = 0;
        try
        {
            while (!_stopped.IsSet)
            {
                if (link is null)
                {
                    link = Device.Wire == LcdWire.Hid ? HidLink.Connect(Device.Path) : LyLink.Connect(Device.Path);
                    // Frames keep arriving meanwhile, so wait on the stop signal, not on them.
                    if (link is null) { _stopped.Wait(ReconnectMs); continue; }
                }

                uint[]? pixels;
                bool motion;
                lock (_gate) { pixels = _pendingPixels; motion = _pendingMotion; _pendingPixels = null; }
                if (pixels is not null && Encode(pixels, motion, link.UpsideDown, ref lastDumpMs) is { } jpeg)
                    frame = link.Frame(jpeg);

                if (frame is not null && !link.Write(frame))
                {
                    Diagnose.Log($"lcd {Device.Id}: frame write failed, reconnecting");
                    link.Dispose();
                    link = null;
                    continue;
                }
                _wake.WaitOne(ResendMs);
            }
        }
        catch (Exception ex)
        {
            Diagnose.Log($"lcd {Device.Id}: sender thread died: {ex}");
        }
        finally
        {
            link?.Dispose();
        }
    }

    private byte[]? Encode(uint[] pixels, bool motion, bool upsideDown, ref long lastDumpMs)
    {
        // A 180° turn of a top-down image is the same pixels in reverse order.
        if (upsideDown) Array.Reverse(pixels);
        var quality = motion ? MotionJpegQuality : JpegQuality;
        var jpeg = JpegEncoder.Encode(pixels, Width, Height, quality);
        while (jpeg is { Length: >= MaxJpegBytes } && quality > 30)
            jpeg = JpegEncoder.Encode(pixels, Width, Height, quality -= 20);

        // The panel can't be screenshotted, so --diagnose keeps an upright copy of what it
        // shows. Motion frames at most once a second, not dozens.
        if (jpeg is not null && Diagnose.IsEnabled && (!motion || Environment.TickCount64 - lastDumpMs >= 1000))
        {
            lastDumpMs = Environment.TickCount64;
            if (upsideDown) Array.Reverse(pixels);
            var copy = upsideDown ? JpegEncoder.Encode(pixels, Width, Height, quality) : jpeg;
            try
            {
                if (copy is not null)
                    File.WriteAllBytes(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lcd-hub-lcd-{Width}x{Height}.jpg"), copy);
            }
            catch (IOException) { }
        }
        return jpeg;
    }

    private static unsafe string[] InterfacePaths(Guid cls)
    {
        if (LcdNative.CM_Get_Device_Interface_List_SizeW(out var len, ref cls, IntPtr.Zero, 0) != 0 || len <= 1)
            return [];
        var buf = new char[len];
        fixed (char* p = buf)
            if (LcdNative.CM_Get_Device_Interface_ListW(ref cls, IntPtr.Zero, p, len, 0) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>An open, handshaken connection to a panel, speaking its wire's protocol.</summary>
    private abstract class Link : IDisposable
    {
        /// <summary>Whether the panel is mounted so that pictures must be sent turned 180°.</summary>
        public bool UpsideDown { get; protected set; }

        /// <summary>Wraps a JPEG in this wire's framing; the result can be resent as is.</summary>
        public abstract byte[] Frame(byte[] jpeg);

        /// <summary>False once the connection is broken.</summary>
        public abstract bool Write(byte[] frame);

        public abstract void Dispose();
    }

    /// <summary>
    /// 0416:5302 over HID: a 20-byte picture header and the JPEG, in 512-byte reports. Opened
    /// for overlapped I/O, so every read/write can time out.
    /// </summary>
    private sealed class HidLink : Link
    {
        private const int ReportLength = 512;
        private const byte PanelId1280x480 = 128;
        private static readonly byte[] Magic = { 0xDA, 0xDB, 0xDC, 0xDD };

        private readonly IntPtr _handle;
        private readonly EventWaitHandle _event = new(false, EventResetMode.ManualReset);
        private readonly int _outLen;
        private readonly int _inLen;

        private HidLink(IntPtr handle, int outLen, int inLen)
        {
            _handle = handle;
            _outLen = outLen;
            _inLen = inLen;
        }

        public static HidLink? Connect(string path)
        {
            var h = LcdNative.CreateFileW(path, LcdNative.GENERIC_READ | LcdNative.GENERIC_WRITE,
                LcdNative.FILE_SHARE_READ | LcdNative.FILE_SHARE_WRITE, IntPtr.Zero,
                LcdNative.OPEN_EXISTING, LcdNative.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == LcdNative.INVALID_HANDLE_VALUE)
            {
                // Most often TRCC holding the device.
                Diagnose.Log($"lcd: open failed ({Marshal.GetLastPInvokeError()}) for {path}");
                return null;
            }

            var caps = default(LcdNative.HIDP_CAPS);
            if (LcdNative.HidD_GetPreparsedData(h, out var pp))
            {
                LcdNative.HidP_GetCaps(pp, ref caps);
                LcdNative.HidD_FreePreparsedData(pp);
            }
            // Windows prefixes every report with its 1-byte report id.
            if (caps.OutputReportByteLength != ReportLength + 1 || caps.InputReportByteLength < 7)
            {
                Diagnose.Log($"lcd: unexpected report sizes out={caps.OutputReportByteLength} in={caps.InputReportByteLength}");
                LcdNative.CloseHandle(h);
                return null;
            }
            var link = new HidLink(h, caps.OutputReportByteLength, caps.InputReportByteLength);

            var init = new byte[ReportLength];
            Magic.CopyTo(init, 0);
            init[12] = 0x01;
            var resp = link.WriteReport(init) ? link.Read(2000) : null;
            if (resp is null || resp.Length < 6 || !resp.AsSpan(0, 4).SequenceEqual(Magic))
            {
                Diagnose.Log($"lcd: handshake failed ({(resp is null ? "no reply" : Convert.ToHexString(resp))})");
                link.Dispose();
                return null;
            }
            if (resp[5] != PanelId1280x480)
            {
                Diagnose.Log($"lcd: unsupported panel PM={resp[5]} SUB={resp[4]}");
                link.Dispose();
                return null;
            }
            Diagnose.Log($"lcd: connected PM={resp[5]} SUB={resp[4]}");
            return link;
        }

        public override byte[] Frame(byte[] jpeg)
        {
            var len = 20 + jpeg.Length;
            var frame = new byte[(len + ReportLength - 1) / ReportLength * ReportLength];
            Magic.CopyTo(frame, 0);
            frame[4] = 0x02; // picture command; [6] = 0 → JPEG payload
            BitConverter.TryWriteBytes(frame.AsSpan(8), (ushort)1280);
            BitConverter.TryWriteBytes(frame.AsSpan(10), (ushort)480);
            frame[12] = 0x02;
            BitConverter.TryWriteBytes(frame.AsSpan(16), jpeg.Length);
            jpeg.CopyTo(frame, 20);
            return frame;
        }

        public override bool Write(byte[] frame)
        {
            for (int off = 0; off < frame.Length; off += ReportLength)
                if (!WriteReport(frame.AsSpan(off, ReportLength))) return false;
            return true;
        }

        private bool WriteReport(ReadOnlySpan<byte> report)
        {
            var buf = new byte[_outLen];
            report.CopyTo(buf.AsSpan(1));
            return Transfer(buf, write: true, 1000) >= ReportLength;
        }

        private byte[]? Read(int timeoutMs)
        {
            var buf = new byte[_inLen];
            var n = Transfer(buf, write: false, timeoutMs);
            return n > 1 ? buf[1..n] : null;
        }

        private unsafe int Transfer(byte[] buf, bool write, int timeoutMs)
        {
            _event.Reset();
            var ov = new NativeOverlapped { EventHandle = _event.SafeWaitHandle.DangerousGetHandle() };
            fixed (byte* p = buf)
            {
                bool ok = write
                    ? LcdNative.WriteFile(_handle, p, buf.Length, out _, &ov)
                    : LcdNative.ReadFile(_handle, p, buf.Length, out _, &ov);
                if (!ok && Marshal.GetLastPInvokeError() != LcdNative.ERROR_IO_PENDING) return -1;
                if (!_event.WaitOne(timeoutMs))
                {
                    LcdNative.CancelIoEx(_handle, &ov);
                    LcdNative.GetOverlappedResult(_handle, &ov, out _, true);
                    return -1;
                }
                return LcdNative.GetOverlappedResult(_handle, &ov, out var n, false) ? n : -1;
            }
        }

        public override void Dispose()
        {
            LcdNative.CloseHandle(_handle);
            _event.Dispose();
        }
    }

    /// <summary>
    /// 0416:5408 over USB bulk: the JPEG in 512-byte chunks (16-byte header + 496 data),
    /// chunk count padded to a multiple of 4, written 4 KB at a time, then a 512-byte ACK
    /// read. The ACK only comes once the firmware has taken the frame, which is what paces
    /// it at ~24 fps.
    /// </summary>
    private sealed unsafe class LyLink : Link
    {
        private const int ChunkSize = 512, ChunkHeader = 16, ChunkData = 496, UsbWrite = 4096;
        private const uint TimeoutMs = 1000, DrainTimeoutMs = 100;

        private readonly IntPtr _file, _usb;
        private readonly byte _outPipe, _inPipe;

        private LyLink(IntPtr file, IntPtr usb, byte outPipe, byte inPipe) =>
            (_file, _usb, _outPipe, _inPipe) = (file, usb, outPipe, inPipe);

        public static LyLink? Connect(string path)
        {
            var file = LcdNative.CreateFileW(path, LcdNative.GENERIC_READ | LcdNative.GENERIC_WRITE,
                LcdNative.FILE_SHARE_READ | LcdNative.FILE_SHARE_WRITE, IntPtr.Zero,
                LcdNative.OPEN_EXISTING, LcdNative.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (file == LcdNative.INVALID_HANDLE_VALUE)
            {
                // WinUSB allows one opener, so most often TRCC holding the device.
                Diagnose.Log($"lcd: open failed ({Marshal.GetLastPInvokeError()}) for {path}");
                return null;
            }
            if (!LcdNative.WinUsb_Initialize(file, out var usb))
            {
                Diagnose.Log($"lcd: WinUsb_Initialize failed ({Marshal.GetLastPInvokeError()})");
                LcdNative.CloseHandle(file);
                return null;
            }

            // trcc-linux writes to endpoint 0x01, but at least one unit takes 0x09; ask.
            byte outPipe = 0, inPipe = 0;
            if (LcdNative.WinUsb_QueryInterfaceSettings(usb, 0, out var iface))
                for (byte i = 0; i < iface.NumEndpoints; i++)
                    if (LcdNative.WinUsb_QueryPipe(usb, 0, i, out var pipe) && pipe.PipeType == LcdNative.UsbdPipeTypeBulk)
                    {
                        if ((pipe.PipeId & 0x80) != 0) inPipe = pipe.PipeId;
                        else outPipe = pipe.PipeId;
                    }
            if (outPipe == 0 || inPipe == 0)
            {
                Diagnose.Log("lcd: no bulk endpoints");
                LcdNative.WinUsb_Free(usb);
                LcdNative.CloseHandle(file);
                return null;
            }
            var link = new LyLink(file, usb, outPipe, inPipe);
            link.SetTimeout(outPipe, TimeoutMs);
            link.Drain();

            var hello = new byte[2048];
            hello[0] = 0x02; hello[1] = 0xFF; hello[8] = 0x01;
            var resp = link.WritePipe(hello) ? link.ReadAck() : null;
            if (resp is null || resp.Length < 37 || resp[0] != 3 || resp[1] != 0xFF || resp[8] != 1)
            {
                Diagnose.Log($"lcd: handshake failed ({(resp is null ? "no reply" : Convert.ToHexString(resp.AsSpan(0, Math.Min(40, resp.Length))))})");
                link.Dispose();
                return null;
            }
            // PM = 64 + resp[20], with ≤ 3 read as 1; 65 and 66 are 1920×462. SUB = resp[22] + 1.
            var pm = 64 + (resp[20] <= 3 ? 1 : resp[20]);
            var sub = resp[22] + 1;
            if (pm is not (65 or 66))
            {
                Diagnose.Log($"lcd: unsupported panel PM={pm} SUB={sub}");
                link.Dispose();
                return null;
            }
            Diagnose.Log($"lcd: connected PM={pm} SUB={sub} out=0x{outPipe:X2}");
            // TRCC sends 1920×462 turned 180° except at SUB 2–4.
            link.UpsideDown = sub is not (2 or 3 or 4);
            return link;
        }

        /// <summary>
        /// Reads off replies nobody collected. The firmware accepts nothing more until its last
        /// reply is read, and one is left behind whenever a sender stops between a frame and its
        /// ACK — killed, or our ACK read timing out — which otherwise locks it out for good.
        /// </summary>
        private void Drain()
        {
            SetTimeout(_inPipe, DrainTimeoutMs);
            var stale = 0;
            while (stale < 8 && ReadAck() is not null) stale++;
            SetTimeout(_inPipe, TimeoutMs);
            if (stale > 0) Diagnose.Log($"lcd: drained {stale} stale repl{(stale == 1 ? "y" : "ies")}");
        }

        private void SetTimeout(byte pipe, uint ms) =>
            LcdNative.WinUsb_SetPipePolicy(_usb, pipe, LcdNative.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref ms);

        public override byte[] Frame(byte[] jpeg)
        {
            var total = jpeg.Length;
            // One more than whole chunks, even when the last is then empty, as TRCC does.
            var chunks = total / ChunkData + 1;
            var frame = new byte[(chunks + 3) / 4 * 4 * ChunkSize];
            for (int i = 0; i < chunks; i++)
            {
                var o = i * ChunkSize;
                var dataLen = i == chunks - 1 ? total % ChunkData : ChunkData;
                frame[o] = 0x01;
                frame[o + 1] = 0xFF;
                BitConverter.TryWriteBytes(frame.AsSpan(o + 2), total);
                BitConverter.TryWriteBytes(frame.AsSpan(o + 6), (ushort)dataLen);
                frame[o + 8] = 0x01;
                BitConverter.TryWriteBytes(frame.AsSpan(o + 9), (ushort)chunks);
                BitConverter.TryWriteBytes(frame.AsSpan(o + 11), (ushort)i);
                jpeg.AsSpan(i * ChunkData, dataLen).CopyTo(frame.AsSpan(o + ChunkHeader));
            }
            return frame;
        }

        public override bool Write(byte[] frame)
        {
            for (int off = 0; off < frame.Length; off += UsbWrite)
                if (!WritePipe(frame.AsSpan(off, Math.Min(UsbWrite, frame.Length - off)))) return false;
            return ReadAck() is not null;
        }

        private bool WritePipe(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
                return LcdNative.WinUsb_WritePipe(_usb, _outPipe, p, (uint)data.Length, out var n, IntPtr.Zero) && n == data.Length;
        }

        private byte[]? ReadAck()
        {
            var buf = new byte[512];
            fixed (byte* p = buf)
                return LcdNative.WinUsb_ReadPipe(_usb, _inPipe, p, (uint)buf.Length, out var n, IntPtr.Zero) ? buf[..(int)n] : null;
        }

        public override void Dispose()
        {
            LcdNative.WinUsb_Free(_usb);
            LcdNative.CloseHandle(_file);
        }
    }
}

/// <summary>BGRX pixels → JPEG bytes via the GDI+ flat API (no COM interop, AOT-safe).</summary>
internal static class JpegEncoder
{
    private static readonly Guid JpegClsid = new("557CF401-1A04-11D3-9A73-0000F81EF32E");
    private static readonly Guid QualityParam = new("1D5BE4B5-FA4A-452D-9CDD-5DB35105E7EB");
    private const int PixelFormat32bppRGB = 0x00022009;
    private const uint EncoderParameterValueTypeLong = 4;

    private static readonly object _startupGate = new();
    private static IntPtr _token;

    /// <summary>Thread-safe; each panel encodes on its own thread.</summary>
    public static unsafe byte[]? Encode(uint[] pixels, int width, int height, long quality)
    {
        lock (_startupGate)
        {
            if (_token == IntPtr.Zero)
            {
                var input = new LcdNative.GdiplusStartupInput { GdiplusVersion = 1 };
                if (LcdNative.GdiplusStartup(out _token, ref input, IntPtr.Zero) != 0) return null;
            }
        }

        IntPtr bitmap = IntPtr.Zero, stream = IntPtr.Zero;
        try
        {
            fixed (uint* scan0 = pixels)
            {
                if (LcdNative.GdipCreateBitmapFromScan0(width, height, width * 4, PixelFormat32bppRGB,
                        (IntPtr)scan0, out bitmap) != 0) return null;
                if (LcdNative.CreateStreamOnHGlobal(IntPtr.Zero, true, out stream) != 0) return null;

                var q = (uint)quality;
                var ps = new LcdNative.EncoderParameters
                {
                    Count = 1,
                    Param = new LcdNative.EncoderParameter
                    {
                        Guid = QualityParam,
                        NumberOfValues = 1,
                        Type = EncoderParameterValueTypeLong,
                        Value = (IntPtr)(&q),
                    },
                };
                var clsid = JpegClsid;
                if (LcdNative.GdipSaveImageToStream(bitmap, stream, ref clsid, (IntPtr)(&ps)) != 0) return null;
            }

            // The HGLOBAL can be larger than what was written; the stream position is the true length.
            ulong size;
            var seek = (delegate* unmanaged[Stdcall]<IntPtr, long, uint, ulong*, int>)(*(IntPtr**)stream)[5];
            if (seek(stream, 0, 1 /* STREAM_SEEK_CUR */, &size) != 0) return null;
            if (LcdNative.GetHGlobalFromStream(stream, out var hglobal) != 0) return null;
            var mem = LcdNative.GlobalLock(hglobal);
            try { return new ReadOnlySpan<byte>((void*)mem, (int)size).ToArray(); }
            finally { LcdNative.GlobalUnlock(hglobal); }
        }
        finally
        {
            if (bitmap != IntPtr.Zero) LcdNative.GdipDisposeImage(bitmap);
            if (stream != IntPtr.Zero) Marshal.Release(stream);
        }
    }
}

internal static unsafe partial class LcdNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_IO_PENDING = 997;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    public const uint PIPE_TRANSFER_TIMEOUT = 0x03;
    public const int UsbdPipeTypeBulk = 2;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct USB_INTERFACE_DESCRIPTOR
    {
        public byte Length, DescriptorType, InterfaceNumber, AlternateSetting, NumEndpoints,
            InterfaceClass, InterfaceSubClass, InterfaceProtocol, Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINUSB_PIPE_INFORMATION
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public fixed ushort Counts[10];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EncoderParameter
    {
        public Guid Guid;
        public uint NumberOfValues;
        public uint Type;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EncoderParameters
    {
        public uint Count;
        public EncoderParameter Param;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(IntPtr h, byte* buf, int len, out int written, NativeOverlapped* ov);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(IntPtr h, byte* buf, int len, out int read, NativeOverlapped* ov);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOverlappedResult(IntPtr h, NativeOverlapped* ov, out int transferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelIoEx(IntPtr h, NativeOverlapped* ov);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr h);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GlobalLock(IntPtr h);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr h);

    [LibraryImport("hid.dll")]
    public static partial void HidD_GetHidGuid(out Guid guid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetPreparsedData(IntPtr h, out IntPtr preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FreePreparsedData(IntPtr preparsed);

    [LibraryImport("hid.dll")]
    public static partial int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid cls, IntPtr deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_Device_Interface_ListW(ref Guid cls, IntPtr deviceId, char* buf, int len, uint flags);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Initialize(IntPtr device, out IntPtr iface);

    [LibraryImport("winusb.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Free(IntPtr iface);

    [LibraryImport("winusb.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryInterfaceSettings(IntPtr iface, byte altIndex, out USB_INTERFACE_DESCRIPTOR descriptor);

    [LibraryImport("winusb.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryPipe(IntPtr iface, byte altIndex, byte pipeIndex, out WINUSB_PIPE_INFORMATION info);

    [LibraryImport("winusb.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_SetPipePolicy(IntPtr iface, byte pipeId, uint policy, uint length, ref uint value);

    [LibraryImport("winusb.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_WritePipe(IntPtr iface, byte pipeId, byte* buf, uint len, out uint transferred, IntPtr overlapped);

    [LibraryImport("winusb.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_ReadPipe(IntPtr iface, byte pipeId, byte* buf, uint len, out uint transferred, IntPtr overlapped);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bitmap);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipSaveImageToStream(IntPtr image, IntPtr stream, ref Guid clsid, IntPtr encoderParams);

    [LibraryImport("gdiplus.dll")]
    public static partial int GdipDisposeImage(IntPtr image);

    [LibraryImport("ole32.dll")]
    public static partial int CreateStreamOnHGlobal(IntPtr hGlobal, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IntPtr stream);

    [LibraryImport("ole32.dll")]
    public static partial int GetHGlobalFromStream(IntPtr stream, out IntPtr hGlobal);
}
