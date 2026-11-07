using System.Diagnostics;
using System.Runtime.Versioning;
using Reshare.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Reshare.Platform.Windows.Native;

namespace Reshare.Platform.Windows;

/// <summary>
/// Captures one monitor with DXGI Desktop Duplication, composites the mouse cursor with GDI
/// and hands out tightly packed BGRX frames.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class DxgiCapture : ICaptureSource
{
    private readonly string _deviceName;
    private readonly int _fps;
    private readonly Rect _bounds;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _staging;

    // GDI DIB section that holds the composed frame (desktop + cursor)
    private nint _dc, _dib, _oldBmp;
    private byte* _bits;
    private byte[] _clean = [];

    public IReadOnlyList<string>? FfmpegInputArgs => null;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public DxgiCapture(DisplayInfo display, int fps)
    {
        _deviceName = display.Id;
        _bounds = display.Bounds;
        _fps = fps;
        Open();
        Log.Debug($"DXGI duplication of {_deviceName}: {Width}x{Height}");
    }

    private void Open()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    using (output)
                    {
                        if (!string.Equals(output.Description.DeviceName, _deviceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                            out _device, out _context).CheckError();

                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        _dup = output1.DuplicateOutput(_device);
                        var mode = _dup.Description.ModeDescription;
                        int w = (int)mode.Width, h = (int)mode.Height;
                        if (Width != 0 && (w != Width || h != Height))
                            throw new ReshareException($"{_deviceName} resolution changed to {w}x{h}; restarting stream");
                        Width = w;
                        Height = h;
                        _staging?.Dispose();
                        _staging = _device.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
                        });
                        if (_dc == 0) CreateDib(w, h);
                        return;
                    }
                }
            }
        }
        throw new ReshareException($"DXGI output {_deviceName} not found");
    }

    private void CreateDib(int w, int h)
    {
        _dc = CreateCompatibleDC(0);
        var bih = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        _dib = CreateDIBSection(_dc, ref bih, 0, out nint bits, 0, 0);
        if (_dib == 0) throw new ReshareException("CreateDIBSection failed");
        _bits = (byte*)bits;
        _oldBmp = SelectObject(_dc, _dib);
        _clean = new byte[w * h * 4];
    }

    private void CloseDuplication()
    {
        _staging?.Dispose(); _staging = null;
        _dup?.Dispose(); _dup = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Run(FrameHandler onFrame, CancellationToken ct)
    {
        timeBeginPeriod(1);
        try { Loop(onFrame, ct); }
        finally { timeEndPeriod(1); }
    }

    private void Loop(FrameHandler onFrame, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double interval = 1000.0 / Math.Max(_fps, 1);
        double nextDue = 0;
        double lastEmit = -1e9;
        bool haveImage = false, dirty = true;
        (nint handle, int x, int y, bool visible) lastCursor = default;
        int frameBytes = Width * Height * 4;

        while (!ct.IsCancellationRequested)
        {
            double wait = nextDue - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);

            if (_dup == null)
            {
                try { Open(); }
                catch (ReshareException) { throw; }
                catch (Exception e) { Log.Debug($"reopen duplication: {e.Message}"); Thread.Sleep(250); continue; }
            }

            Result r = _dup!.AcquireNextFrame(haveImage ? 20u : 500u, out OutduplFrameInfo info, out IDXGIResource? resource);
            if (r.Success)
            {
                try
                {
                    if (info.LastPresentTime != 0 || !haveImage)
                    {
                        using var tex = resource!.QueryInterface<ID3D11Texture2D>();
                        _context!.CopyResource(_staging!, tex);
                        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            fixed (byte* dst = _clean)
                            {
                                byte* src = (byte*)map.DataPointer;
                                int row = Width * 4;
                                for (int y = 0; y < Height; y++)
                                    Buffer.MemoryCopy(src + (long)y * map.RowPitch, dst + (long)y * row, row, row);
                            }
                        }
                        finally { _context.Unmap(_staging!, 0); }
                        haveImage = true;
                        dirty = true;
                    }
                }
                finally
                {
                    resource?.Dispose();
                    _dup.ReleaseFrame();
                }
            }
            else if (r.Code == Vortice.DXGI.ResultCode.AccessLost.Code || r.Code == Vortice.DXGI.ResultCode.InvalidCall.Code)
            {
                // desktop switch (UAC, lock screen), mode change, ... -> recreate
                Log.Debug($"duplication lost (0x{r.Code:X8}), reopening");
                CloseDuplication();
                Thread.Sleep(100);
                continue;
            }
            else if (r.Code != Vortice.DXGI.ResultCode.WaitTimeout.Code)
            {
                r.CheckError();
            }

            if (!haveImage) continue;

            var cur = new CURSORINFO { cbSize = sizeof(CURSORINFO) };
            GetCursorInfo(ref cur);
            var cursor = (cur.hCursor, cur.ptScreenPos.X, cur.ptScreenPos.Y, (cur.flags & CURSOR_SHOWING) != 0);
            bool cursorOnScreen = cursor.Item4 && _bounds.Contains(cursor.X, cursor.Y);
            if (cursor != lastCursor && (cursorOnScreen || (lastCursor.visible && _bounds.Contains(lastCursor.x, lastCursor.y))))
                dirty = true;
            lastCursor = cursor;

            double now = sw.Elapsed.TotalMilliseconds;
            // Re-send periodically even when idle so encoders never sit on a buffered frame.
            if (!dirty && now - lastEmit < 250) continue;

            fixed (byte* src = _clean) Buffer.MemoryCopy(src, _bits, frameBytes, frameBytes);
            if (cursorOnScreen && GetIconInfo(cur.hCursor, out var ii))
            {
                DrawIconEx(_dc, cursor.X - _bounds.X - ii.xHotspot, cursor.Y - _bounds.Y - ii.yHotspot, cur.hCursor, 0, 0, 0, 0, DI_NORMAL);
                if (ii.hbmMask != 0) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != 0) DeleteObject(ii.hbmColor);
                GdiFlush();
            }
            onFrame(new ReadOnlySpan<byte>(_bits, frameBytes));
            dirty = false;
            lastEmit = now;
            nextDue = Math.Max(nextDue + interval, now - interval);
        }
    }

    public void Dispose()
    {
        CloseDuplication();
        if (_dc != 0)
        {
            SelectObject(_dc, _oldBmp);
            DeleteObject(_dib);
            DeleteDC(_dc);
            _dc = 0;
        }
    }
}
