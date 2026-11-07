using System.Diagnostics;
using System.Globalization;
using Reshare.Core;
using Reshare.Core.Capture;

namespace Reshare.Platform.MacOS;

/// <summary>
/// macOS host: virtual monitor via the reshare-macvd helper (CGVirtualDisplay), capture via ffmpeg
/// avfoundation, input via CGEvent. Needs Screen Recording + Accessibility permissions for the terminal.
/// </summary>
public sealed class MacPlatform : IPlatform
{
    public string Name => "macOS";
    public IReadOnlyList<string> PreferredEncoders => ["h264_videotoolbox", "libx264"];

    public IReadOnlyList<DisplayInfo> ListDisplays()
    {
        uint main = CG.CGMainDisplayID();
        var ids = CG.ActiveDisplays();
        return ids.Select((id, index) =>
        {
            var b = CG.CGDisplayBounds(id);
            var rect = new Rect((int)b.Origin.X, (int)b.Origin.Y, (int)b.Size.Width, (int)b.Size.Height);
            bool builtin = CG.CGDisplayIsBuiltin(id) != 0;
            // index = avfoundation "Capture screen N" index (same order as CGGetActiveDisplayList)
            return new DisplayInfo(id.ToString(CultureInfo.InvariantCulture), builtin ? "Built-in display" : $"Display {id}",
                rect, id == main, false) { Native = index };
        }).ToList();
    }

    public async Task<IDisplayLease> AcquireDisplayAsync(HostOptions o, CancellationToken ct)
    {
        if (o.Display != "auto")
        {
            var all = ListDisplays();
            DisplayInfo? d = o.Display == "primary" ? all.FirstOrDefault(x => x.IsPrimary)
                : int.TryParse(o.Display, out int n) && n >= 1 && n <= all.Count ? all[n - 1]
                : all.FirstOrDefault(x => x.Id == o.Display);
            return new NoopLease(d ?? throw new ReshareException($"Display '{o.Display}' not found. Use --list-displays."));
        }

        string helper = FindHelper() ?? throw new ReshareException(
            "reshare-macvd helper not found. Build it with:\n" +
            "    cd native/macos/reshare-macvd && swift build -c release\n" +
            "  and copy .build/release/reshare-macvd next to reshare-host (or put it on PATH).\n" +
            "  To mirror an existing screen instead, use --display primary.");

        var psi = new ProcessStartInfo(helper)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "--width", o.Width.ToString(CultureInfo.InvariantCulture), "--height", o.Height.ToString(CultureInfo.InvariantCulture),
                                  "--hz", Math.Min(o.Fps, 60).ToString(CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new ReshareException("failed to start reshare-macvd");
        _ = Task.Run(async () => { string? l; while ((l = await proc.StandardError.ReadLineAsync()) != null) Log.Debug("macvd: " + l); });

        string? line = await proc.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (line == null || !line.StartsWith("DISPLAY ") || !uint.TryParse(line[8..], out uint id))
        {
            try { proc.Kill(); } catch { }
            throw new ReshareException($"reshare-macvd failed: {line ?? "no output"}");
        }

        for (int i = 0; i < 40; i++)
        {
            var d = ListDisplays().FirstOrDefault(x => x.Id == id.ToString(CultureInfo.InvariantCulture));
            if (d != null)
            {
                Log.Info($"Virtual display {id} created ({d.Bounds})");
                return new HelperLease(d with { Name = "Reshare virtual display", IsVirtual = true }, proc);
            }
            await Task.Delay(250, ct);
        }
        try { proc.Kill(); } catch { }
        throw new ReshareException("Virtual display was created but never became active");
    }

    private static string? FindHelper()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "reshare-macvd");
        return File.Exists(local) ? local : Core.Util.Proc.Which("reshare-macvd");
    }

    private sealed class HelperLease(DisplayInfo display, Process proc) : IDisplayLease
    {
        public DisplayInfo Display { get; } = display;
        public async ValueTask DisposeAsync()
        {
            try
            {
                proc.StandardInput.Close(); // helper exits on stdin EOF, removing the display
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { try { proc.Kill(); } catch { } }
            proc.Dispose();
        }
    }

    public ICaptureSource CreateCapture(DisplayInfo d, HostOptions o) => new FfmpegNativeCapture(
    [
        "-f", "avfoundation", "-capture_cursor", "1", "-capture_mouse_clicks", "0",
        "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture),
        "-i", $"Capture screen {(int)(d.Native ?? 0)}:none",
    ]);

    public IInputInjector CreateInputInjector(DisplayInfo display) => new MacInputInjector(display);
}
