using System.Globalization;

namespace Reshare.Core;

public sealed class HostOptions
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public int Port { get; set; } = 8765;
    public int BitrateKbps { get; set; } = 12000;
    /// <summary>auto | libx264 | h264_nvenc | h264_qsv | h264_amf | h264_videotoolbox</summary>
    public string Encoder { get; set; } = "auto";
    /// <summary>auto (virtual display) | primary | index (1-based, as in --list-displays) | platform id</summary>
    public string Display { get; set; } = "auto";
    public string? Pin { get; set; }
    public bool NoPin { get; set; }
    public bool Http { get; set; }
    public bool TestPattern { get; set; }
    public bool ListDisplays { get; set; }
    public bool BenchCapture { get; set; }
    public bool KeepDisplay { get; set; }
    public bool NoInput { get; set; }
    public string? FfmpegPath { get; set; }
    public bool Verbose { get; set; }
    public bool Help { get; set; }

    public const string Usage = """
        reshare-host - use another laptop (any browser) as an extended monitor

        Usage: reshare-host [options]

          --width <px>          virtual monitor width              (default 1920)
          --height <px>         virtual monitor height             (default 1080)
          --fps <n>             max frames per second              (default 60)
          --bitrate <kbps>      video bitrate                      (default 12000)
          --port <n>            HTTPS port                         (default 8765)
          --display <sel>       auto | primary | <n> | <id>        (default auto = virtual display)
          --encoder <name>      auto | libx264 | h264_nvenc | h264_qsv | h264_amf | h264_videotoolbox
          --ffmpeg <path>       path to ffmpeg executable          (default: next to app, then PATH)
          --pin <digits>        fixed pairing PIN                  (default: random 6 digits)
          --no-pin              disable PIN (trusted networks only)
          --http                serve plain HTTP (viewer must be localhost or have the
                                insecure-origin browser flag, WebCodecs needs a secure context)
          --keep-display        do not remove the virtual display on exit
          --no-input            ignore mouse/keyboard from the viewer
          --test-pattern        stream a generated test pattern instead of a screen
          --list-displays       print displays and exit
          --bench-capture       capture the selected display for 5 s, print fps, exit
          -v, --verbose         verbose logging
          -h, --help            show this help
        """;

    public static HostOptions Parse(string[] args)
    {
        var o = new HostOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ReshareException($"Missing value for {a}");
                return args[++i];
            }
            int NextInt(int min, int max)
            {
                string v = Next();
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
                    throw new ReshareException($"Invalid value '{v}' for {a} (expected {min}..{max})");
                return n;
            }
            switch (a)
            {
                case "--width": o.Width = NextInt(320, 7680); break;
                case "--height": o.Height = NextInt(200, 4320); break;
                case "--fps": o.Fps = NextInt(1, 240); break;
                case "--bitrate": o.BitrateKbps = NextInt(250, 200_000); break;
                case "--port": o.Port = NextInt(1, 65535); break;
                case "--display": o.Display = Next(); break;
                case "--encoder": o.Encoder = Next(); break;
                case "--ffmpeg": o.FfmpegPath = Next(); break;
                case "--pin":
                    o.Pin = Next();
                    if (o.Pin.Length is < 4 or > 12 || !o.Pin.All(char.IsAsciiDigit))
                        throw new ReshareException("--pin must be 4-12 digits");
                    break;
                case "--no-pin": o.NoPin = true; break;
                case "--http": o.Http = true; break;
                case "--keep-display": o.KeepDisplay = true; break;
                case "--no-input": o.NoInput = true; break;
                case "--test-pattern": o.TestPattern = true; break;
                case "--list-displays": o.ListDisplays = true; break;
                case "--bench-capture": o.BenchCapture = true; break;
                case "-v" or "--verbose": o.Verbose = true; break;
                case "-h" or "--help" or "/?": o.Help = true; break;
                default: throw new ReshareException($"Unknown option '{a}'. Use --help.");
            }
        }
        // yuv420 needs even dimensions
        o.Width &= ~1;
        o.Height &= ~1;
        return o;
    }
}
