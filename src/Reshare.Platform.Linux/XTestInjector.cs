using System.Runtime.InteropServices;
using Reshare.Core;
using Reshare.Core.Input;

namespace Reshare.Platform.Linux;

internal sealed class XTestInjector : InputInjectorBase
{
    private const string LibX11 = "libX11.so.6", LibXtst = "libXtst.so.6";

    [DllImport(LibX11)] private static extern nint XOpenDisplay(nint name);
    [DllImport(LibX11)] private static extern int XCloseDisplay(nint d);
    [DllImport(LibX11)] private static extern int XFlush(nint d);
    [DllImport(LibX11)] private static extern int XDefaultScreen(nint d);
    [DllImport(LibX11)] private static extern nuint XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string s);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(nint d, nuint keysym);
    [DllImport(LibXtst)] private static extern int XTestFakeMotionEvent(nint d, int screen, int x, int y, nuint delay);
    [DllImport(LibXtst)] private static extern int XTestFakeButtonEvent(nint d, uint button, int isPress, nuint delay);
    [DllImport(LibXtst)] private static extern int XTestFakeKeyEvent(nint d, uint keycode, int isPress, nuint delay);

    private readonly DisplayInfo _display;
    private readonly nint _x;
    private readonly object _gate = new();

    public XTestInjector(DisplayInfo display)
    {
        _display = display;
        _x = XOpenDisplay(0);
        if (_x == 0) throw new ReshareException("XOpenDisplay failed (DISPLAY not set?)");
    }

    protected override void DoMove(double nx, double ny)
    {
        var (x, y) = Coordinates.ToAbsolute(_display.Bounds, nx, ny);
        lock (_gate) { XTestFakeMotionEvent(_x, -1, x, y, 0); XFlush(_x); }
    }

    protected override void DoButton(MouseButton button, bool down)
    {
        uint b = button switch { MouseButton.Left => 1, MouseButton.Middle => 2, MouseButton.Right => 3, MouseButton.Back => 8, _ => 9 };
        lock (_gate) { XTestFakeButtonEvent(_x, b, down ? 1 : 0, 0); XFlush(_x); }
    }

    protected override void DoWheel(int x, int y)
    {
        // X11 wheel is click based: buttons 4/5 vertical, 6/7 horizontal. 120 units = 1 click.
        void Clicks(ref int acc, int units, uint neg, uint pos)
        {
            acc += units;
            int n = acc / 120;
            acc -= n * 120;
            for (int i = 0; i < Math.Min(Math.Abs(n), 20); i++)
            {
                uint b = n < 0 ? neg : pos;
                XTestFakeButtonEvent(_x, b, 1, 0);
                XTestFakeButtonEvent(_x, b, 0, 0);
            }
        }
        lock (_gate) { Clicks(ref _accY, y, 4, 5); Clicks(ref _accX, x, 6, 7); XFlush(_x); }
    }

    private int _accX, _accY;

    protected override void DoKey(string domCode, bool down)
    {
        if (!KeyMaps.X11.TryGetValue(domCode, out var name)) return;
        lock (_gate)
        {
            byte code = XKeysymToKeycode(_x, XStringToKeysym(name));
            if (code == 0) return;
            XTestFakeKeyEvent(_x, code, down ? 1 : 0, 0);
            XFlush(_x);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        XCloseDisplay(_x);
    }
}
