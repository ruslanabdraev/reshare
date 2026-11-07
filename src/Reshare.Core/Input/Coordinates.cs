namespace Reshare.Core.Input;

public static class Coordinates
{
    /// <summary>Normalized (0..1) position on a display -> absolute desktop pixel.</summary>
    public static (int X, int Y) ToAbsolute(Rect display, double nx, double ny)
    {
        nx = double.IsFinite(nx) ? Math.Clamp(nx, 0, 1) : 0;
        ny = double.IsFinite(ny) ? Math.Clamp(ny, 0, 1) : 0;
        int x = display.X + (int)Math.Round(nx * (display.Width - 1));
        int y = display.Y + (int)Math.Round(ny * (display.Height - 1));
        return (x, y);
    }

    /// <summary>
    /// Absolute desktop pixel -> Windows SendInput coordinates with MOUSEEVENTF_VIRTUALDESK
    /// (0..65535 spanning the whole virtual screen, whose origin may be negative).
    /// </summary>
    public static (int Dx, int Dy) ToWindowsVirtualDesk(int x, int y, Rect virtualScreen)
    {
        int Map(int v, int origin, int size) =>
            size <= 1 ? 0 : (int)Math.Round((v - origin) * 65535.0 / (size - 1));
        return (Math.Clamp(Map(x, virtualScreen.X, virtualScreen.Width), 0, 65535),
                Math.Clamp(Map(y, virtualScreen.Y, virtualScreen.Height), 0, 65535));
    }
}
