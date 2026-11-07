using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Reshare.Core;
using Reshare.Core.Capture;
using Reshare.Core.Input;
using Reshare.Core.Video;
using Reshare.Host;
using HostOptions = Reshare.Core.HostOptions;
using Reshare.Platform.Linux;
using Reshare.Platform.MacOS;
using Reshare.Platform.Windows;

try
{
    return await RunAsync(args);
}
catch (ReshareException e)
{
    Log.Error(e.Message);
    return 1;
}
catch (OperationCanceledException)
{
    return 0;
}

static IPlatform CreatePlatform()
{
    if (OperatingSystem.IsWindows()) return new WindowsPlatform();
    if (OperatingSystem.IsMacOS()) return new MacPlatform();
    if (OperatingSystem.IsLinux()) return new LinuxPlatform();
    throw new ReshareException("Unsupported operating system");
}

static async Task<int> RunAsync(string[] args)
{
    var o = HostOptions.Parse(args);
    if (o.Help) { Console.WriteLine(HostOptions.Usage); return 0; }
    Log.Verbose = o.Verbose;

    var platform = CreatePlatform();
    if (o.ListDisplays)
    {
        var list = platform.ListDisplays();
        for (int i = 0; i < list.Count; i++) Console.WriteLine($"{i + 1,2}. {list[i]}");
        return 0;
    }
    if (o.BenchCapture)
    {
        await BenchCaptureAsync(platform, o);
        return 0;
    }

    string ffmpeg = Ffmpeg.Locate(o.FfmpegPath) ?? throw new ReshareException(
        "ffmpeg not found. Install it and make sure it is on PATH (or pass --ffmpeg <path>):\n    " +
        (OperatingSystem.IsWindows() ? "winget install Gyan.FFmpeg"
         : OperatingSystem.IsMacOS() ? "brew install ffmpeg" : "sudo apt install ffmpeg   (or your distro's equivalent)"));
    Log.Debug(Ffmpeg.Version(ffmpeg));
    string encoder = Ffmpeg.SelectEncoder(ffmpeg, o.Encoder, o.TestPattern ? ["libx264"] : platform.PreferredEncoders, o.Fps, o.BitrateKbps);
    Log.Info($"Encoder: {encoder}");

    // Not disposed on purpose: the exit handlers below may still fire after this method returns.
    var shutdown = new CancellationTokenSource();
    var cleanedUp = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    // Console window closed / SIGTERM: give the lease a chance to remove the virtual display.
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { shutdown.Cancel(); cleanedUp.Wait(3000); };

    try
    {
        await using IDisplayLease lease = o.TestPattern
            ? new NoopLease(new DisplayInfo("test", "Test pattern", new Rect(0, 0, o.Width, o.Height), false, true))
            : await platform.AcquireDisplayAsync(o, shutdown.Token);
        var display = lease.Display;
        Log.Info($"Streaming display: {display}");

        Func<ICaptureSource> captureFactory = o.TestPattern
            ? () => new TestPatternSource(o.Width, o.Height, o.Fps)
            : () => platform.CreateCapture(display, o);
        using (captureFactory()) { } // fail fast if capture can't be set up

        Func<IInputInjector> injectorFactory = o.TestPattern || o.NoInput
            ? () => new LoggingInjector()
            : () => platform.CreateInputInjector(display);

        string? pin = o.NoPin ? null : o.Pin ?? RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        await RunServerAsync(o, platform, display, captureFactory, injectorFactory, ffmpeg, encoder, pin, shutdown.Token);
    }
    finally
    {
        cleanedUp.Set();
    }
    Log.Info("Stopped");
    return 0;
}

static async Task RunServerAsync(HostOptions o, IPlatform platform, DisplayInfo display, Func<ICaptureSource> captureFactory,
    Func<IInputInjector> injectorFactory, string ffmpeg, string encoder, string? pin, CancellationToken shutdown)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = [],
        ContentRootPath = AppContext.BaseDirectory,
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    });
    builder.Logging.ClearProviders();
    if (o.Verbose) builder.Logging.AddSimpleConsole(c => c.SingleLine = true).SetMinimumLevel(LogLevel.Warning);

    var cert = o.Http ? null : Certificates.GetOrCreate();
    builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(o.Port, l =>
    {
        // Raw TCP accept, before TLS: tells "never arrived" apart from "TLS handshake failed".
        l.Use(next => async conn =>
        {
            Log.Debug($"tcp connection from {conn.RemoteEndPoint}");
            try { await next(conn); }
            catch (Exception e) { Log.Debug($"connection {conn.RemoteEndPoint} failed: {e.GetBaseException().Message}"); throw; }
        });
        if (cert != null)
            l.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions
            {
                ServerCertificate = cert,
                OnAuthenticate = (conn, ssl) => Log.Debug($"tls handshake starting with {conn.RemoteEndPoint}"),
            });
    }));

    var app = builder.Build();
    app.Use(async (ctx, next) =>
    {
        Log.Debug($"{ctx.Connection.RemoteIpAddress} {ctx.Request.Protocol} {ctx.Request.Method} {ctx.Request.Path} ({ctx.Request.Headers.UserAgent})");
        await next(ctx);
    });
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

    string hostOs = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";
    app.MapGet("/config", () => Results.Json(new
    {
        name = Environment.MachineName,
        os = hostOs,
        display = display.Name,
        width = display.Bounds.Width,
        height = display.Bounds.Height,
        pinRequired = pin != null,
    }));

    var slots = new ViewerSlots();
    int pinFailures = 0;

    async Task<WebSocket?> AcceptAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return null; }
        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        if (pin != null && !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(ctx.Request.Query["pin"].ToString()), Encoding.UTF8.GetBytes(pin)))
        {
            // slow down brute force
            await Task.Delay(Math.Min(500 * Interlocked.Increment(ref pinFailures), 5000));
            try { await ws.CloseOutputAsync((WebSocketCloseStatus)4001, "bad pin", CancellationToken.None); }
            catch (WebSocketException) { }
            ws.Dispose();
            return null;
        }
        return ws;
    }

    app.Map("/stream", async ctx =>
    {
        using var ws = await AcceptAsync(ctx);
        if (ws == null) return;
        var remote = ctx.Connection.RemoteIpAddress;
        using var session = slots.Claim(ref slots.Stream, shutdown);
        Log.Success($"Viewer connected: {remote}");
        try
        {
            await StreamSession.RunAsync(ws, captureFactory, ffmpeg, encoder, o, session.Token, () =>
                shutdown.IsCancellationRequested ? (WebSocketCloseStatus.EndpointUnavailable, "host stopped")
                : session.IsCancellationRequested ? ((WebSocketCloseStatus)4002, "replaced by another viewer")
                : (WebSocketCloseStatus.InternalServerError, "stream ended"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"stream failed: {e.Message}");
            Log.Debug(e.ToString());
            try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, Truncate(e.Message, 120), CancellationToken.None); } catch { }
        }
        Log.Info($"Viewer disconnected: {remote}");
    });

    app.Map("/input", async ctx =>
    {
        using var ws = await AcceptAsync(ctx);
        if (ws == null) return;
        using var session = slots.Claim(ref slots.Input, shutdown);
        using var injector = injectorFactory();
        var token = session.Token;
        var buf = new byte[4096];
        var msg = new MemoryStream();
        try
        {
            await SendText(ws, JsonSerializer.Serialize(new { t = "hello", os = hostOs }), token);
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) { if (msg.Length > 65536) break; continue; }
                string text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                msg.SetLength(0);
                try
                {
                    if (InputProtocol.Handle(text, injector) is { } reply) await SendText(ws, reply, token);
                }
                catch (JsonException) { }
                catch (Exception e) { Log.Debug($"input error: {e.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    });

    await app.StartAsync(shutdown);
    PrintBanner(o, display, cert != null, pin);
    try { await Task.Delay(Timeout.Infinite, shutdown); }
    catch (OperationCanceledException) { }
    Log.Info("Shutting down...");
    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await app.StopAsync(stopTimeout.Token);
}

static async Task BenchCaptureAsync(IPlatform platform, HostOptions o)
{
    await using var lease = await platform.AcquireDisplayAsync(o, CancellationToken.None);
    using var capture = platform.CreateCapture(lease.Display, o);
    if (capture.FfmpegInputArgs != null)
    {
        Log.Info("This platform captures inside ffmpeg; nothing to benchmark: " + string.Join(' ', capture.FfmpegInputArgs));
        return;
    }
    Log.Info($"Capturing {lease.Display.Id} ({capture.Width}x{capture.Height}) for 5 s at up to {o.Fps} fps. Move a window or the mouse on it...");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    int frames = 0, expected = capture.Width * capture.Height * 4, badSize = 0;
    long nonBlack = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Task.Run(() => capture.Run(f =>
    {
        frames++;
        if (f.Length != expected) badSize++;
        if (f[f.Length / 2] != 0 || f[f.Length / 2 + 1] != 0) nonBlack++;
    }, cts.Token));
    Log.Info($"{frames} frames in {sw.Elapsed.TotalSeconds:F1} s = {frames / sw.Elapsed.TotalSeconds:F1} fps, " +
             $"{badSize} wrong-size, {nonBlack} with non-black center pixel");
}

static Task SendText(WebSocket ws, string text, CancellationToken ct) =>
    ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

static void PrintBanner(HostOptions o, DisplayInfo display, bool https, string? pin)
{
    string scheme = https ? "https" : "http";
    string q = pin != null ? $"/?pin={pin}" : "/";
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("  Reshare is running. On the other laptop, open one of:");
    foreach (var ip in Certificates.LanAddresses())
        Console.WriteLine($"     {scheme}://{ip}:{o.Port}{q}");
    Console.ResetColor();
    if (pin != null) Console.WriteLine($"  PIN: {pin}");
    if (https) Console.WriteLine("  The certificate is self-signed: accept the browser warning once (Advanced > Proceed).");
    Console.WriteLine($"  Display: {display.Bounds.Width}x{display.Bounds.Height}  -  press Ctrl+C to stop.");
    Console.WriteLine();
}

/// <summary>One viewer at a time: a new connection replaces the previous one.</summary>
internal sealed class ViewerSlots
{
    public CancellationTokenSource? Stream;
    public CancellationTokenSource? Input;
    private readonly object _gate = new();

    public CancellationTokenSource Claim(ref CancellationTokenSource? slot, CancellationToken shutdown)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        CancellationTokenSource? old;
        lock (_gate) { old = slot; slot = cts; }
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
        return cts;
    }
}
