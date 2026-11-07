namespace Reshare.Core;

/// <summary>Integer rectangle in the platform's input/desktop coordinate space.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
    public override string ToString() => $"{Width}x{Height}@({X},{Y})";
}

/// <summary>A monitor known to the host OS.</summary>
/// <param name="Id">Platform id (Windows: \\.\DISPLAYn, macOS: CGDirectDisplayID, Linux: xrandr output name).</param>
/// <param name="Name">Human readable adapter/monitor name.</param>
/// <param name="Bounds">Position and size in the coordinate space used for input injection.</param>
public sealed record DisplayInfo(string Id, string Name, Rect Bounds, bool IsPrimary, bool IsVirtual)
{
    /// <summary>Optional platform specific data (e.g. avfoundation screen index).</summary>
    public object? Native { get; init; }

    public override string ToString() =>
        $"{Id,-14} {Bounds,-24} {(IsPrimary ? "primary " : "")}{(IsVirtual ? "virtual " : "")}{Name}";
}

/// <summary>Ownership of the display being streamed; disposing undoes whatever was set up (virtual monitor, mode change).</summary>
public interface IDisplayLease : IAsyncDisposable
{
    DisplayInfo Display { get; }
}

public sealed class NoopLease(DisplayInfo display) : IDisplayLease
{
    public DisplayInfo Display { get; } = display;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Receives one tightly packed BGRX frame (Width*Height*4 bytes).</summary>
public delegate void FrameHandler(ReadOnlySpan<byte> bgrx);

/// <summary>
/// A screen capture source. Either produces raw frames itself (<see cref="FfmpegInputArgs"/> is null)
/// or tells ffmpeg how to capture natively (x11grab, avfoundation, ...).
/// </summary>
public interface ICaptureSource : IDisposable
{
    IReadOnlyList<string>? FfmpegInputArgs { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>Blocking capture loop for raw sources. Runs on a dedicated thread until cancelled.</summary>
    void Run(FrameHandler onFrame, CancellationToken ct);
}

public enum MouseButton { Left = 0, Middle = 1, Right = 2, Back = 3, Forward = 4 }

public interface IInputInjector : IDisposable
{
    /// <summary>Move the pointer to a position given in 0..1 normalized coordinates of the streamed display.</summary>
    void MoveTo(double nx, double ny);
    void Button(MouseButton button, bool down);
    /// <summary>Scroll, in DOM convention: CSS pixels, positive dy = scroll down, positive dx = scroll right.</summary>
    void Wheel(double dx, double dy);
    /// <summary>Key press/release identified by DOM <c>KeyboardEvent.code</c>.</summary>
    void Key(string domCode, bool down);
    void ReleaseAll();
}

public interface IPlatform
{
    string Name { get; }
    IReadOnlyList<DisplayInfo> ListDisplays();

    /// <summary>Find or create the display to stream according to <see cref="HostOptions.Display"/>.</summary>
    Task<IDisplayLease> AcquireDisplayAsync(HostOptions options, CancellationToken ct);

    ICaptureSource CreateCapture(DisplayInfo display, HostOptions options);
    IInputInjector CreateInputInjector(DisplayInfo display);

    /// <summary>ffmpeg encoders to try, best first.</summary>
    IReadOnlyList<string> PreferredEncoders { get; }
}

public sealed class ReshareException(string message) : Exception(message);
