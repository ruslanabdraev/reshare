using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Reshare.Core;
using Reshare.Core.Input;
using static Reshare.Platform.Windows.Native;

namespace Reshare.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsInputInjector(DisplayInfo display) : InputInjectorBase
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private static void Send(INPUT input)
    {
        if (SendInput(1, [input], InputSize) == 0)
            Log.Debug($"SendInput blocked (error {Marshal.GetLastWin32Error()}); target window may be elevated");
    }

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) =>
        new() { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data } } };

    protected override void DoMove(double nx, double ny)
    {
        var (x, y) = Coordinates.ToAbsolute(display.Bounds, nx, ny);
        var (dx, dy) = Coordinates.ToWindowsVirtualDesk(x, y, WindowsDisplays.VirtualScreen());
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, dx, dy));
    }

    protected override void DoButton(MouseButton button, bool down)
    {
        var input = button switch
        {
            MouseButton.Left => Mouse(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP),
            MouseButton.Right => Mouse(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => Mouse(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP),
            MouseButton.Back => Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, data: 1),
            _ => Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, data: 2),
        };
        Send(input);
    }

    protected override void DoWheel(int x, int y)
    {
        // Windows: positive WHEEL = scroll up, positive HWHEEL = scroll right.
        if (y != 0) Send(Mouse(MOUSEEVENTF_WHEEL, data: unchecked((uint)-y)));
        if (x != 0) Send(Mouse(MOUSEEVENTF_HWHEEL, data: unchecked((uint)x)));
    }

    protected override void DoKey(string domCode, bool down)
    {
        if (!KeyMaps.Windows.TryGetValue(domCode, out var k)) { Log.Debug($"unmapped key {domCode}"); return; }
        uint flags = (k.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = k.Vk, wScan = (ushort)MapVirtualKeyW(k.Vk, 0), dwFlags = flags } },
        });
    }
}
