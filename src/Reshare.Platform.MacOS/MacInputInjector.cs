using System.Diagnostics;
using Reshare.Core;
using Reshare.Core.Input;

namespace Reshare.Platform.MacOS;

internal sealed class MacInputInjector(DisplayInfo display) : InputInjectorBase
{
    private CG.CGPoint _pos = new() { X = display.Bounds.X, Y = display.Bounds.Y };
    private readonly HashSet<MouseButton> _held = new();
    private ulong _flags;
    private long _lastClickTicks;
    private MouseButton _lastClickButton;
    private int _clickCount;

    private static void Post(nint ev)
    {
        if (ev == 0) return;
        CG.CGEventPost(CG.HIDEventTap, ev);
        CG.CFRelease(ev);
    }

    protected override void DoMove(double nx, double ny)
    {
        var (x, y) = Coordinates.ToAbsolute(display.Bounds, nx, ny);
        _pos = new CG.CGPoint { X = x, Y = y };
        // While a button is held macOS expects "dragged" events, otherwise drags don't work.
        (uint type, uint btn) = _held.Contains(MouseButton.Left) ? (CG.LeftMouseDragged, 0u)
            : _held.Contains(MouseButton.Right) ? (CG.RightMouseDragged, 1u)
            : _held.Count > 0 ? (CG.OtherMouseDragged, 2u)
            : (CG.MouseMoved, 0u);
        Post(CG.CGEventCreateMouseEvent(0, type, _pos, btn));
    }

    protected override void DoButton(MouseButton button, bool down)
    {
        if (down)
        {
            long now = Stopwatch.GetTimestamp();
            bool repeat = button == _lastClickButton && Stopwatch.GetElapsedTime(_lastClickTicks, now).TotalMilliseconds < 450;
            _clickCount = repeat ? _clickCount + 1 : 1;
            _lastClickTicks = now;
            _lastClickButton = button;
            _held.Add(button);
        }
        else _held.Remove(button);

        (uint type, uint btn) = button switch
        {
            MouseButton.Left => (down ? CG.LeftMouseDown : CG.LeftMouseUp, 0u),
            MouseButton.Right => (down ? CG.RightMouseDown : CG.RightMouseUp, 1u),
            MouseButton.Middle => (down ? CG.OtherMouseDown : CG.OtherMouseUp, 2u),
            MouseButton.Back => (down ? CG.OtherMouseDown : CG.OtherMouseUp, 3u),
            _ => (down ? CG.OtherMouseDown : CG.OtherMouseUp, 4u),
        };
        var ev = CG.CGEventCreateMouseEvent(0, type, _pos, btn);
        if (ev == 0) return;
        CG.CGEventSetIntegerValueField(ev, CG.MouseEventClickState, _clickCount);
        if (btn > 1) CG.CGEventSetIntegerValueField(ev, CG.MouseEventButtonNumber, btn);
        CG.CGEventSetFlags(ev, _flags);
        Post(ev);
    }

    protected override void DoWheel(int x, int y)
    {
        // units: 120 per notch (Windows convention) -> pixels; macOS positive = scroll up / left
        Post(CG.CGEventCreateScrollWheelEvent2(0, 0 /* pixel */, 2, -y * 100 / 120, -x * 100 / 120, 0));
    }

    protected override void DoKey(string domCode, bool down)
    {
        if (!KeyMaps.MacOS.TryGetValue(domCode, out ushort key)) return;
        ulong flag = domCode switch
        {
            "ShiftLeft" or "ShiftRight" => CG.FlagShift,
            "ControlLeft" or "ControlRight" => CG.FlagControl,
            "AltLeft" or "AltRight" => CG.FlagAlternate,
            "MetaLeft" or "MetaRight" => CG.FlagCommand,
            _ => 0,
        };
        if (flag != 0) _flags = down ? _flags | flag : _flags & ~flag;
        var ev = CG.CGEventCreateKeyboardEvent(0, key, down);
        if (ev == 0) return;
        CG.CGEventSetFlags(ev, _flags);
        Post(ev);
    }
}
