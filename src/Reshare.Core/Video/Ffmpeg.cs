using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Reshare.Core.Video;

public static class Ffmpeg
{
    /// <summary>Find a working ffmpeg: explicit path, RESHARE_FFMPEG, next to the app, then PATH.</summary>
    public static string? Locate(string? explicitPath)
    {
        string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitPath)) candidates.Add(explicitPath);
        if (Environment.GetEnvironmentVariable("RESHARE_FFMPEG") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", exe));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim('"'), exe));
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add("/opt/homebrew/bin/ffmpeg");
            candidates.Add("/usr/local/bin/ffmpeg");
            candidates.Add("/usr/bin/ffmpeg");
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string PixelFormatFor(string encoder) => encoder switch
    {
        "h264_qsv" or "h264_amf" or "h264_videotoolbox" => "nv12",
        _ => "yuv420p",
    };

    /// <summary>Encoder specific low-latency arguments.</summary>
    public static IEnumerable<string> EncoderArgs(string encoder, int fps, int bitrateKbps)
    {
        string b = bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k";
        string buf = Math.Max(bitrateKbps / 2, 100).ToString(CultureInfo.InvariantCulture) + "k";
        string gop = Math.Max(fps, 1).ToString(CultureInfo.InvariantCulture);
        IEnumerable<string> specific = encoder switch
        {
            "libx264" => ["-preset", "ultrafast", "-tune", "zerolatency"],
            "h264_nvenc" => ["-preset", "p1", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1", "-delay", "0"],
            "h264_qsv" => ["-preset", "veryfast", "-async_depth", "1"],
            "h264_amf" => ["-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr"],
            "h264_videotoolbox" => ["-realtime", "1", "-allow_sw", "1"],
            _ => [],
        };
        return ["-c:v", encoder, .. specific, "-bf", "0", "-g", gop, "-b:v", b, "-maxrate", b, "-bufsize", buf];
    }

    public static readonly IReadOnlyList<string> FlvPipeOutput =
        ["-f", "flv", "-flvflags", "no_duration_filesize", "pipe:1"];

    public static List<string> BuildArgs(IReadOnlyList<string> inputArgs, string encoder, int fps, int bitrateKbps,
        IReadOnlyList<string>? outputArgs = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(inputArgs);
        args.AddRange(["-an", "-vf", $"crop=trunc(iw/2)*2:trunc(ih/2)*2,format={PixelFormatFor(encoder)}"]);
        args.AddRange(EncoderArgs(encoder, fps, bitrateKbps));
        args.AddRange(["-fps_mode", "passthrough", "-flush_packets", "1"]);
        args.AddRange(outputArgs ?? FlvPipeOutput);
        return args;
    }

    public static List<string> RawInputArgs(int width, int height, int fps) =>
        ["-f", "rawvideo", "-pix_fmt", "bgr0", "-video_size", $"{width}x{height}",
         "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0"];

    /// <summary>Pick the first encoder that actually works on this machine.</summary>
    public static string SelectEncoder(string ffmpeg, string requested, IReadOnlyList<string> preferred, int fps, int bitrate)
    {
        var list = requested == "auto" ? preferred.Append("libx264").Distinct().ToList() : [requested];
        foreach (var enc in list)
        {
            if (Probe(ffmpeg, enc, fps, bitrate, out string err))
                return enc;
            Log.Debug($"encoder {enc} unavailable: {err.Trim().Split('\n').LastOrDefault()}");
        }
        throw new ReshareException(requested == "auto"
            ? "No working H.264 encoder found in ffmpeg (need libx264 or a hardware encoder)."
            : $"Encoder '{requested}' does not work with this ffmpeg/GPU.");
    }

    private static bool Probe(string ffmpeg, string encoder, int fps, int bitrate, out string error)
    {
        var args = BuildArgs(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30"], encoder, fps, bitrate,
            ["-frames:v", "5", "-f", "null", "-"]);
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } error = "timeout"; return false; }
            error = errTask.Result;
            return p.ExitCode == 0;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    public static string Version(string ffmpeg)
    {
        var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string first = p.StandardOutput.ReadLine() ?? "";
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return first;
    }
}

/// <summary>One running ffmpeg process: raw frames (or native capture) in, FLV/H.264 out.</summary>
public sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private int _disposed;

    public FfmpegEncoder(string ffmpeg, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Log.Debug("ffmpeg " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        _process = Process.Start(psi) ?? throw new ReshareException("Failed to start ffmpeg");
        _stdin = _process.StandardInput.BaseStream;
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync()) != null)
                if (line.Length > 0) Log.Warn("ffmpeg: " + line);
        });
    }

    public Stream Output => _process.StandardOutput.BaseStream;
    public bool HasExited => _process.HasExited;

    public void WriteFrame(ReadOnlySpan<byte> frame)
    {
        _stdin.Write(frame);
        _stdin.Flush();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stdin.Dispose(); } catch { }
        try
        {
            if (!_process.WaitForExit(500)) _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process.Dispose();
    }
}
