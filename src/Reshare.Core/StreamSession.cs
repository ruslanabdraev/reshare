using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using Reshare.Core.Video;

namespace Reshare.Core;

/// <summary>
/// One viewer connection on /stream: capture -> ffmpeg -> WebSocket.
/// Binary messages to the viewer:
///   [0x01][avcC]                          decoder configuration
///   [0x02|0x03][u64 BE timestamp µs][AVCC] key (0x02) or delta (0x03) frame
/// </summary>
public static class StreamSession
{
    public const byte MsgConfig = 1, MsgKey = 2, MsgDelta = 3;

    /// <param name="closeReason">Evaluated when the session ends, to tell the viewer why (e.g. replaced by another viewer).</param>
    public static async Task RunAsync(WebSocket ws, Func<ICaptureSource> captureFactory, string ffmpeg, string encoder,
        HostOptions o, CancellationToken ct, Func<(WebSocketCloseStatus Status, string Reason)>? closeReason = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        using var capture = captureFactory();
        var inputArgs = capture.FfmpegInputArgs ?? Ffmpeg.RawInputArgs(capture.Width, capture.Height, o.Fps);
        using var enc = new FfmpegEncoder(ffmpeg, Ffmpeg.BuildArgs(inputArgs, encoder, o.Fps, o.BitrateKbps));

        Thread? captureThread = null;
        if (capture.FfmpegInputArgs == null)
        {
            captureThread = new Thread(() =>
            {
                try { capture.Run(frame => enc.WriteFrame(frame), token); }
                catch (Exception e) when (token.IsCancellationRequested && e is IOException or OperationCanceledException or ObjectDisposedException) { }
                catch (IOException) { Log.Warn("ffmpeg stopped accepting frames"); }
                catch (Exception e) { Log.Error($"capture failed: {e.Message}"); Log.Debug(e.ToString()); }
                finally { cts.Cancel(); }
            }) { IsBackground = true, Name = "capture" };
            captureThread.Start();
        }

        var channel = Channel.CreateBounded<VideoPacket>(new BoundedChannelOptions(240)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var pkt in new FlvReader(enc.Output).ReadAsync(token))
                    await channel.Writer.WriteAsync(pkt, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (!token.IsCancellationRequested) { Log.Error($"encoder output: {e.Message}"); }
            finally
            {
                channel.Writer.TryComplete();
                if (!token.IsCancellationRequested && enc.HasExited) Log.Warn("ffmpeg exited");
                cts.Cancel();
            }
        });

        // Drain incoming messages so we notice when the viewer closes the socket.
        // No cancellation token here: cancelling a WebSocket receive aborts the socket, and we want a clean close.
        var receiver = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
            finally { cts.Cancel(); }
        });

        int maxBacklog = Math.Max(o.Fps / 4, 4); // ~250 ms
        bool haveConfig = false, dropping = false;
        long frames = 0, dropped = 0, bytes = 0;
        var stats = Stopwatch.StartNew();
        try
        {
            await foreach (var pkt in channel.Reader.ReadAllAsync(token))
            {
                if (pkt.Kind == AvcPacketKind.SequenceHeader)
                {
                    await SendConfig(ws, pkt.Data, token);
                    haveConfig = true;
                    continue;
                }
                if (!haveConfig)
                {
                    if (!pkt.IsKey || Avc.BuildAvcCFromKeyframe(pkt.Data) is not { } avcC) continue;
                    await SendConfig(ws, avcC, token);
                    haveConfig = true;
                }

                // Under congestion skip delta frames until the next key frame, rather than letting latency grow.
                if (pkt.IsKey) dropping = false;
                else if (dropping || channel.Reader.Count > maxBacklog) { dropping = true; dropped++; continue; }

                var msg = new byte[9 + pkt.Data.Length];
                msg[0] = pkt.IsKey ? MsgKey : MsgDelta;
                BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(1), (ulong)Math.Max(0, pkt.TimestampMs) * 1000);
                pkt.Data.CopyTo(msg, 9);
                await ws.SendAsync(msg, WebSocketMessageType.Binary, true, token);
                frames++;
                bytes += msg.Length;

                if (stats.ElapsedMilliseconds >= 5000)
                {
                    double s = stats.Elapsed.TotalSeconds;
                    Log.Debug($"stream: {frames / s:F1} fps, {bytes * 8 / s / 1000:F0} kbit/s, {dropped} dropped");
                    frames = bytes = dropped = 0;
                    stats.Restart();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            cts.Cancel();
            enc.Dispose(); // unblocks capture thread writes and the reader
            await reader.WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => { });
            captureThread?.Join(3000);
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var (status, reason) = closeReason?.Invoke() ?? (WebSocketCloseStatus.NormalClosure, "");
                try { await ws.CloseOutputAsync(status, reason, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)); }
                catch { }
            }
            // wait briefly for the viewer's close reply, then give up
            if (!await receiver.WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(t => t.IsCompletedSuccessfully))
                ws.Abort();
        }
    }

    private static Task SendConfig(WebSocket ws, byte[] avcC, CancellationToken ct)
    {
        Log.Debug($"stream: decoder config {Avc.CodecString(avcC)}");
        var msg = new byte[1 + avcC.Length];
        msg[0] = MsgConfig;
        avcC.CopyTo(msg, 1);
        return ws.SendAsync(msg, WebSocketMessageType.Binary, true, ct);
    }
}
