using System.Globalization;
using System.Text.RegularExpressions;
using Reshare.Core;
using Reshare.Core.Capture;
using Reshare.Core.Util;

namespace Reshare.Platform.Linux;

/// <summary>
/// X11 host: virtual monitor via an xrandr VIRTUAL output, capture via ffmpeg x11grab, input via XTest.
/// Wayland sessions are not supported (no portable virtual-output API).
/// </summary>
public sealed partial class LinuxPlatform : IPlatform
{
    private static string XDisplay => Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } d ? d : ":0";

    public LinuxPlatform()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase))
            Log.Warn("Wayland session detected: only XWayland is reachable. Log in with an X11 (Xorg) session for full support.");
    }

    public string Name => "Linux/X11";
    public IReadOnlyList<string> PreferredEncoders => ["h264_nvenc", "libx264"];

    [GeneratedRegex(@"^(?<name>\S+) (?<state>connected|disconnected)(?<primary> primary)?(?: (?<w>\d+)x(?<h>\d+)\+(?<x>-?\d+)\+(?<y>-?\d+))?")]
    private static partial Regex OutputLine();

    private sealed record XOutput(string Name, bool Connected, bool Primary, Rect? Geometry);

    private static List<XOutput> QueryOutputs()
    {
        var r = Proc.Run("xrandr", "--query");
        if (!r.Ok) throw new ReshareException("xrandr failed. Is DISPLAY set and xrandr installed? " + r.Stderr.Trim());
        var list = new List<XOutput>();
        foreach (var line in r.Stdout.Split('\n'))
        {
            var m = OutputLine().Match(line);
            if (!m.Success) continue;
            Rect? geo = m.Groups["w"].Success
                ? new Rect(int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                           int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture))
                : null;
            list.Add(new XOutput(m.Groups["name"].Value, m.Groups["state"].Value == "connected", m.Groups["primary"].Success, geo));
        }
        return list;
    }

    private static bool IsVirtual(string name) => name.StartsWith("VIRTUAL", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DisplayInfo> ListDisplays() =>
        QueryOutputs().Where(o => o.Geometry != null)
            .Select(o => new DisplayInfo(o.Name, o.Name, o.Geometry!.Value, o.Primary, IsVirtual(o.Name)))
            .ToList();

    public async Task<IDisplayLease> AcquireDisplayAsync(HostOptions o, CancellationToken ct)
    {
        var all = ListDisplays();
        if (o.Display != "auto")
        {
            DisplayInfo? d = o.Display == "primary" ? all.FirstOrDefault(x => x.IsPrimary) ?? all.FirstOrDefault()
                : int.TryParse(o.Display, out int n) && n >= 1 && n <= all.Count ? all[n - 1]
                : all.FirstOrDefault(x => x.Id == o.Display);
            return new NoopLease(d ?? throw new ReshareException($"Display '{o.Display}' not found. Use --list-displays."));
        }

        if (all.FirstOrDefault(d => d.IsVirtual) is { } active) return new NoopLease(active);

        var outputs = QueryOutputs();
        var virt = outputs.FirstOrDefault(x => IsVirtual(x.Name))
            ?? throw new ReshareException(
                "No xrandr VIRTUAL output found. Enable one in your Xorg config, e.g. for Intel:\n" +
                "    Section \"Device\"  Identifier \"intel\"  Driver \"intel\"  Option \"VirtualHeads\" \"1\"  EndSection\n" +
                "  (amdgpu/nouveau: use the 'evdi' kernel module), or use --display primary to mirror.");
        string anchor = (outputs.FirstOrDefault(x => x.Primary) ?? outputs.First(x => x.Geometry != null)).Name;

        string mode = $"reshare_{o.Width}x{o.Height}";
        var cvt = Proc.Run("cvt", o.Width.ToString(CultureInfo.InvariantCulture), o.Height.ToString(CultureInfo.InvariantCulture), Math.Min(o.Fps, 60).ToString(CultureInfo.InvariantCulture));
        var ml = Regex.Match(cvt.Stdout, "Modeline\\s+\"[^\"]+\"\\s+(.+)");
        if (!ml.Success) throw new ReshareException("'cvt' failed to compute a modeline (install x11-xserver-utils / xorg-xrandr).");
        var newMode = new List<string> { "--newmode", mode };
        newMode.AddRange(ml.Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Proc.Run("xrandr", [.. newMode]);                 // fails harmlessly if it already exists
        Proc.Run("xrandr", "--addmode", virt.Name, mode);
        var on = Proc.Run("xrandr", "--output", virt.Name, "--mode", mode, "--right-of", anchor);
        if (!on.Ok) throw new ReshareException($"Failed to enable {virt.Name}: {on.Stderr.Trim()}");
        Log.Info($"Enabled {virt.Name} {o.Width}x{o.Height} right of {anchor}");

        for (int i = 0; i < 20; i++)
        {
            if (ListDisplays().FirstOrDefault(d => d.Id == virt.Name) is { } d)
                return new XrandrLease(d, o.KeepDisplay);
            await Task.Delay(250, ct);
        }
        throw new ReshareException($"{virt.Name} did not become active");
    }

    private sealed class XrandrLease(DisplayInfo display, bool keep) : IDisplayLease
    {
        public DisplayInfo Display { get; } = display;
        public ValueTask DisposeAsync()
        {
            if (!keep) Proc.Run("xrandr", "--output", Display.Id, "--off");
            return ValueTask.CompletedTask;
        }
    }

    public ICaptureSource CreateCapture(DisplayInfo d, HostOptions o) => new FfmpegNativeCapture(
    [
        "-f", "x11grab", "-draw_mouse", "1", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture),
        "-video_size", $"{d.Bounds.Width}x{d.Bounds.Height}",
        "-i", $"{XDisplay}+{d.Bounds.X},{d.Bounds.Y}",
    ]);

    public IInputInjector CreateInputInjector(DisplayInfo display) => new XTestInjector(display);
}
