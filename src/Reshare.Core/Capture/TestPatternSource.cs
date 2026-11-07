using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Reshare.Core.Capture;

/// <summary>Generated moving pattern, for testing the pipeline without any display setup.</summary>
public sealed class TestPatternSource(int width, int height, int fps) : ICaptureSource
{
    public IReadOnlyList<string>? FfmpegInputArgs => null;
    public int Width { get; } = width;
    public int Height { get; } = height;

    public void Run(FrameHandler onFrame, CancellationToken ct)
    {
        var frame = new uint[Width * Height];
        // Pre-render a horizontal hue gradient twice as wide so rows can be scrolled by slicing.
        var gradient = new uint[Width * 2];
        for (int x = 0; x < gradient.Length; x++)
            gradient[x] = Hue((x % Width) / (double)Width);

        var sw = Stopwatch.StartNew();
        long interval = Stopwatch.Frequency / Math.Max(fps, 1);
        long next = sw.ElapsedTicks;
        int n = 0;
        while (!ct.IsCancellationRequested)
        {
            int shift = (n * 4) % Width;
            for (int y = 0; y < Height; y++)
            {
                var row = frame.AsSpan(y * Width, Width);
                gradient.AsSpan(shift, Width).CopyTo(row);
                if (y % 120 == 0) row.Fill(0xFF202020);
            }
            // bouncing white box
            int box = Math.Min(Width, Height) / 6;
            int bx = Bounce(n * 7, Width - box), by = Bounce(n * 5, Height - box);
            for (int y = by; y < by + box; y++) frame.AsSpan(y * Width + bx, box).Fill(0xFFFFFFFF);
            // frame counter as a binary bar along the top
            for (int bit = 0; bit < 16; bit++)
                frame.AsSpan(8 * Width + 8 + bit * 24, 20).Fill(((n >> (15 - bit)) & 1) != 0 ? 0xFFFFFFFFu : 0xFF000000u);

            onFrame(MemoryMarshal.AsBytes(frame.AsSpan()));
            n++;

            next += interval;
            long wait = next - sw.ElapsedTicks;
            if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            else next = sw.ElapsedTicks;
        }
    }

    private static int Bounce(int v, int max) => max <= 0 ? 0 : (v / max) % 2 == 0 ? v % max : max - v % max;

    private static uint Hue(double h)
    {
        double r = Math.Clamp(Math.Abs(h * 6 - 3) - 1, 0, 1);
        double g = Math.Clamp(2 - Math.Abs(h * 6 - 2), 0, 1);
        double b = Math.Clamp(2 - Math.Abs(h * 6 - 4), 0, 1);
        return 0xFF000000u | ((uint)(r * 200) << 16) | ((uint)(g * 200) << 8) | (uint)(b * 200);
    }

    public void Dispose() { }
}
