using System.Buffers.Binary;
using Reshare.Core;
using Reshare.Core.Input;
using Reshare.Core.Video;
using Xunit;

namespace Reshare.Core.Tests;

public class CoordinateTests
{
    [Theory]
    [InlineData(0.0, 0.0, 1920, 0)]
    [InlineData(1.0, 1.0, 3839, 1079)]
    [InlineData(0.5, 0.5, 2880, 540)]
    [InlineData(-3.0, 9.0, 1920, 1079)]   // clamped
    public void NormalizedMapsIntoDisplayBounds(double nx, double ny, int ex, int ey)
    {
        var display = new Rect(1920, 0, 1920, 1080);
        Assert.Equal((ex, ey), Coordinates.ToAbsolute(display, nx, ny));
    }

    [Fact]
    public void NaNIsSafe() => Assert.Equal((10, 20), Coordinates.ToAbsolute(new Rect(10, 20, 100, 100), double.NaN, double.NaN));

    [Fact]
    public void VirtualDeskHandlesNegativeOrigin()
    {
        // second monitor left of the primary: virtual screen spans -1920..1919
        var vs = new Rect(-1920, 0, 3840, 1080);
        Assert.Equal((0, 0), Coordinates.ToWindowsVirtualDesk(-1920, 0, vs));
        Assert.Equal((65535, 65535), Coordinates.ToWindowsVirtualDesk(1919, 1079, vs));
        var (dx, _) = Coordinates.ToWindowsVirtualDesk(0, 0, vs);
        Assert.InRange(dx, 32760, 32790); // ~middle
    }
}

public class KeyMapTests
{
    private static readonly string[] Common =
    [
        "KeyA", "KeyZ", "Digit0", "Digit9", "Enter", "Escape", "Backspace", "Tab", "Space", "Minus", "Equal",
        "BracketLeft", "BracketRight", "Backslash", "Semicolon", "Quote", "Backquote", "Comma", "Period", "Slash",
        "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "PageUp", "PageDown", "Delete",
        "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight",
        "F1", "F12", "Numpad0", "Numpad9", "NumpadEnter", "NumpadAdd", "CapsLock",
    ];

    [Fact]
    public void AllPlatformsMapCommonKeys()
    {
        foreach (var code in Common)
        {
            Assert.True(KeyMaps.Windows.ContainsKey(code), $"Windows missing {code}");
            Assert.True(KeyMaps.MacOS.ContainsKey(code), $"macOS missing {code}");
            Assert.True(KeyMaps.X11.ContainsKey(code), $"X11 missing {code}");
        }
    }

    [Fact]
    public void WindowsCodesAreSane()
    {
        Assert.Equal((ushort)'A', KeyMaps.Windows["KeyA"].Vk);
        Assert.Equal((ushort)0x26, KeyMaps.Windows["ArrowUp"].Vk);
        Assert.True(KeyMaps.Windows["ArrowUp"].Extended);
        Assert.False(KeyMaps.Windows["Enter"].Extended);
        Assert.True(KeyMaps.Windows["NumpadEnter"].Extended);
    }

    [Fact]
    public void MacCodesAreUnique() =>
        Assert.Equal(KeyMaps.MacOS.Count, KeyMaps.MacOS.Values.Distinct().Count());
}

public class InputProtocolTests
{
    private sealed class Recorder : InputInjectorBase
    {
        public readonly List<string> Events = new();
        protected override void DoMove(double nx, double ny) => Events.Add($"move {nx} {ny}");
        protected override void DoButton(MouseButton b, bool down) => Events.Add($"{b} {(down ? "down" : "up")}");
        protected override void DoWheel(int x, int y) => Events.Add($"wheel {x} {y}");
        protected override void DoKey(string code, bool down) => Events.Add($"{code} {(down ? "down" : "up")}");
    }

    [Fact]
    public void DispatchesMessages()
    {
        var r = new Recorder();
        InputProtocol.Handle("""{"t":"mm","x":0.25,"y":0.75}""", r);
        InputProtocol.Handle("""{"t":"md","b":2}""", r);
        InputProtocol.Handle("""{"t":"mu","b":2}""", r);
        InputProtocol.Handle("""{"t":"kd","code":"KeyQ"}""", r);
        InputProtocol.Handle("""{"t":"ku","code":"KeyQ"}""", r);
        InputProtocol.Handle("""{"t":"wh","dx":0,"dy":100}""", r);
        Assert.Equal(["move 0.25 0.75", "Right down", "Right up", "KeyQ down", "KeyQ up", "wheel 0 120"], r.Events);
    }

    [Fact]
    public void PingReturnsPong()
    {
        var reply = InputProtocol.Handle("""{"t":"ping","ts":12.5}""", new Recorder());
        Assert.Equal("""{"t":"pong","ts":12.5}""", reply);
    }

    [Fact]
    public void ReleaseAllReleasesHeldOnly()
    {
        var r = new Recorder();
        r.Key("ShiftLeft", true);
        r.Button(MouseButton.Left, true);
        r.Key("KeyA", true);
        r.Key("KeyA", false);
        r.Events.Clear();
        InputProtocol.Handle("""{"t":"release"}""", r);
        Assert.Equal(["ShiftLeft up", "Left up"], r.Events);
        r.Key("KeyB", false); // never pressed -> ignored
        Assert.Equal(2, r.Events.Count);
    }

    [Fact]
    public void SmallWheelDeltasAccumulate()
    {
        var r = new Recorder();
        for (int i = 0; i < 10; i++) r.Wheel(0, 0.5);
        int total = r.Events.Where(e => e.StartsWith("wheel")).Sum(e => int.Parse(e.Split(' ')[2]));
        Assert.InRange(total, 5, 6); // 10 x 0.5px x 1.2 = 6 units, remainder carried
    }

    [Fact]
    public void IgnoresJunk()
    {
        var r = new Recorder();
        Assert.Null(InputProtocol.Handle("""{"t":"md","b":99}""", r));
        Assert.Null(InputProtocol.Handle("""[1,2]""", r));
        Assert.Null(InputProtocol.Handle("""{"x":1}""", r));
        Assert.Empty(r.Events);
    }
}

public class FlvTests
{
    private static byte[] Tag(byte type, uint ts, byte[] body)
    {
        var t = new byte[11 + body.Length + 4];
        t[0] = type;
        t[1] = (byte)(body.Length >> 16); t[2] = (byte)(body.Length >> 8); t[3] = (byte)body.Length;
        t[4] = (byte)(ts >> 16); t[5] = (byte)(ts >> 8); t[6] = (byte)ts; t[7] = (byte)(ts >> 24);
        body.CopyTo(t, 11);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(11 + body.Length), (uint)(11 + body.Length));
        return t;
    }

    [Fact]
    public async Task ParsesVideoTagsAndSkipsOthers()
    {
        var sps = new byte[] { 0x67, 0x64, 0x00, 0x1F, 0xAC };
        var pps = new byte[] { 0x68, 0xEE, 0x3C };
        var avcC = Avc.BuildAvcC(sps, pps);
        var frame = new byte[] { 0, 0, 0, 2, 0x65, 0x88 };

        var ms = new MemoryStream();
        ms.Write("FLV"u8);
        ms.Write([1, 1, 0, 0, 0, 9]);   // version, video flag, header size 9
        ms.Write([0, 0, 0, 0]);         // PreviousTagSize0
        // remove trailing PreviousTagSize from each tag since the reader consumes it before the next tag
        void WriteTag(byte[] tag) => ms.Write(tag, 0, tag.Length - 4);
        WriteTag(Tag(18, 0, [2, 0, 1, (byte)'x']));                           // script data, ignored
        ms.Write([0, 0, 0, 15]);
        WriteTag(Tag(9, 0, [0x17, 0, 0, 0, 0, .. avcC]));                       // sequence header
        ms.Write([0, 0, 0, 0]);
        WriteTag(Tag(9, 33, [0x17, 1, 0, 0, 0, .. frame]));                     // key frame
        ms.Write([0, 0, 0, 0]);
        WriteTag(Tag(9, 0x01000010, [0x27, 1, 0, 0, 0, .. frame]));             // delta, extended timestamp
        ms.Position = 0;

        var packets = new List<VideoPacket>();
        await foreach (var p in new FlvReader(ms).ReadAsync()) packets.Add(p);

        Assert.Equal(3, packets.Count);
        Assert.Equal(AvcPacketKind.SequenceHeader, packets[0].Kind);
        Assert.Equal(avcC, packets[0].Data);
        Assert.True(packets[1].IsKey);
        Assert.Equal(33, packets[1].TimestampMs);
        Assert.Equal(frame, packets[1].Data);
        Assert.False(packets[2].IsKey);
        Assert.Equal(0x01000010, packets[2].TimestampMs);
    }

    [Fact]
    public void AvcCFromInBandParameterSets()
    {
        var sps = new byte[] { 0x67, 0x42, 0xC0, 0x1E, 0x01 };
        var pps = new byte[] { 0x68, 0xCE };
        var idr = new byte[] { 0x65, 0x11, 0x22 };
        var buf = new List<byte>();
        foreach (var n in new[] { sps, pps, idr })
        {
            buf.AddRange([0, 0, 0, (byte)n.Length]);
            buf.AddRange(n);
        }
        var avcC = Avc.BuildAvcCFromKeyframe(buf.ToArray());
        Assert.NotNull(avcC);
        Assert.Equal("avc1.42c01e", Avc.CodecString(avcC));
        Assert.Null(Avc.BuildAvcCFromKeyframe([0, 0, 0, 3, .. idr]));
    }
}

public class OptionsTests
{
    [Fact]
    public void ParsesAndRoundsToEven()
    {
        var o = HostOptions.Parse(["--width", "1367", "--height", "769", "--fps", "30", "--display", "primary", "--no-pin"]);
        Assert.Equal(1366, o.Width);
        Assert.Equal(768, o.Height);
        Assert.Equal(30, o.Fps);
        Assert.Equal("primary", o.Display);
        Assert.True(o.NoPin);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--fps", "0")]
    [InlineData("--pin", "12ab")]
    [InlineData("--width")]
    public void RejectsBadInput(params string[] args) => Assert.Throws<ReshareException>(() => HostOptions.Parse(args));

    [Fact]
    public void EncoderArgsAreLowLatency()
    {
        var args = Ffmpeg.BuildArgs(Ffmpeg.RawInputArgs(1920, 1080, 60), "libx264", 60, 8000);
        string s = string.Join(' ', args);
        Assert.Contains("-tune zerolatency", s);
        Assert.Contains("-bf 0", s);
        Assert.Contains("-video_size 1920x1080", s);
        Assert.EndsWith("-f flv -flvflags no_duration_filesize pipe:1", s);
    }
}
