using System.Runtime.InteropServices;

namespace Reshare.Platform.MacOS;

internal static class CG
{
    private const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)] public struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CGSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)] public struct CGRect { public CGPoint Origin; public CGSize Size; }

    [DllImport(Lib)] public static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[]? activeDisplays, out uint displayCount);
    [DllImport(Lib)] public static extern CGRect CGDisplayBounds(uint display);
    [DllImport(Lib)] public static extern uint CGMainDisplayID();
    [DllImport(Lib)] public static extern int CGDisplayIsBuiltin(uint display);
    [DllImport(Lib)] public static extern nuint CGDisplayPixelsWide(uint display);

    // Event types
    public const uint LeftMouseDown = 1, LeftMouseUp = 2, RightMouseDown = 3, RightMouseUp = 4, MouseMoved = 5,
        LeftMouseDragged = 6, RightMouseDragged = 7, OtherMouseDown = 25, OtherMouseUp = 26, OtherMouseDragged = 27;
    public const uint HIDEventTap = 0;
    public const int MouseEventClickState = 1, MouseEventButtonNumber = 3;
    public const ulong FlagShift = 0x20000, FlagControl = 0x40000, FlagAlternate = 0x80000, FlagCommand = 0x100000;

    [DllImport(Lib)] public static extern nint CGEventCreateMouseEvent(nint source, uint type, CGPoint pos, uint button);
    [DllImport(Lib)] public static extern nint CGEventCreateKeyboardEvent(nint source, ushort keycode, [MarshalAs(UnmanagedType.U1)] bool keyDown);
    [DllImport(Lib)] public static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(Lib)] public static extern void CGEventSetIntegerValueField(nint ev, int field, long value);
    [DllImport(Lib)] public static extern void CGEventSetFlags(nint ev, ulong flags);
    [DllImport(Lib)] public static extern void CGEventPost(uint tap, nint ev);
    [DllImport(CF)] public static extern void CFRelease(nint obj);

    public static uint[] ActiveDisplays()
    {
        CGGetActiveDisplayList(0, null, out uint count);
        var ids = new uint[count];
        CGGetActiveDisplayList(count, ids, out count);
        return ids.Take((int)count).ToArray();
    }
}
