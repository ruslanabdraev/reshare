# Reshare

Use a second laptop as an **extended monitor** for your main computer. The second laptop
just opens a web page — no install needed there — and its mouse and keyboard control the extended screen.

```
 main laptop (host)                                      second laptop (viewer)
 ┌────────────────────────────┐   H.264 over WSS     ┌───────────────────────────┐
 │ virtual monitor ─► capture │ ───────────────────► │ browser (WebCodecs) shows │
 │ ─► ffmpeg encode ─► server │ ◄─────────────────── │ the screen fullscreen     │
 │ ◄─ input injection         │   mouse / keyboard   │                           │
 └────────────────────────────┘                      └───────────────────────────┘
```

## Quick start (Windows host)

1. **Install the prerequisites on the main laptop**
   - [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build) 
   - ffmpeg: `winget install Gyan.FFmpeg` (or put `ffmpeg.exe` next to `reshare-host.exe`)
   - A virtual display driver, so Windows believes a second monitor is plugged in. The free, signed
     [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases) works well
     (Parsec VDD, spacedesk's driver or a dummy HDMI plug also work).
2. **Build & run**
   ```powershell
   dotnet publish src/Reshare.Host -c Release -o publish
   .\publish\reshare-host.exe --width 1920 --height 1080
   ```
   The host finds the virtual monitor (enabling the driver if needed when run as Administrator),
   sets the resolution and prints an address and PIN.
3. **On the second laptop**, open the printed `https://<ip>:8765/?pin=…` URL in Chrome/Edge/Safari/Firefox,
   accept the self-signed certificate warning once, and click **Fullscreen**.
4. Drag windows onto the new monitor. Press **Ctrl+C** in the host console to stop; the virtual monitor is removed again.

Both laptops must be on the same network (5 GHz Wi-Fi or Ethernet recommended). Allow `reshare-host`
through the firewall when Windows asks (Private networks).

## Options

```
--width/--height <px>   virtual monitor size (default 1920x1080)
--fps <n>               max frame rate (default 60)
--bitrate <kbps>        video bitrate (default 12000)
--port <n>              HTTPS port (default 8765)
--display <sel>         auto (virtual display) | primary | <n> | <id>; --display primary mirrors your main screen
--encoder <name>        auto | libx264 | h264_nvenc | h264_qsv | h264_amf | h264_videotoolbox
--pin <digits>/--no-pin fixed PIN / no PIN
--no-input              view only
--test-pattern          stream a generated pattern (no display needed), good for testing the network
--list-displays         show displays
--bench-capture         measure capture fps of the selected display
-v                      verbose logging (fps, bitrate, ffmpeg command line)
```

In the viewer: **F9** toggles stats (fps, bitrate, round-trip time). "Local cursor" shows the viewer's own
pointer in addition to the streamed one; "⌘ → Ctrl" (Mac viewers) sends Command as Ctrl.

## macOS host

```sh
brew install ffmpeg
cd native/macos/reshare-macvd && swift build -c release && cd -
dotnet publish src/Reshare.Host -c Release -o publish
cp native/macos/reshare-macvd/.build/release/reshare-macvd publish/
./publish/reshare-host
```

The `reshare-macvd` helper creates a virtual display with CoreGraphics' `CGVirtualDisplay` API (private but
widely used, e.g. by DeskPad). Grant your terminal **Screen Recording** (capture) and **Accessibility** (input)
permissions in System Settings › Privacy & Security.

## Linux host (X11)

Needs `ffmpeg`, `xrandr`, `cvt` and `libxtst`. Reshare enables an xrandr `VIRTUAL*` output (Intel: Xorg option
`"VirtualHeads" "1"`; other GPUs: the `evdi` module). Without one, use `--display primary` to mirror.
Wayland sessions aren't supported.

## How it works

| Piece | Implementation |
|---|---|
| Virtual monitor | Windows: indirect display driver (IddCx) controlled via PnP + `ChangeDisplaySettingsEx`; macOS: `CGVirtualDisplay` helper; Linux: xrandr VIRTUAL output |
| Capture | Windows: DXGI Desktop Duplication + GDI cursor compositing (`Reshare.Platform.Windows/DxgiCapture.cs`); macOS/Linux: ffmpeg `avfoundation` / `x11grab` |
| Encode | ffmpeg subprocess, hardware encoder if available (NVENC/QSV/AMF/VideoToolbox) else x264, zero-latency settings, no B-frames, 1 s GOP, FLV over a pipe |
| Transport | ASP.NET Core Kestrel, HTTPS + WebSockets: `/stream` (binary: avcC config + AVCC frames), `/input` (JSON) |
| Viewer | `wwwroot/viewer.js`: WebCodecs `VideoDecoder` → canvas; pointer/keyboard → normalized JSON events |
| Input | Windows `SendInput`, macOS `CGEventPost`, Linux XTest |

WebCodecs only works on secure origins, which is why the host serves HTTPS with a self-signed certificate
(stored in `%LOCALAPPDATA%\Reshare`). Under congestion the host drops frames up to the next key frame rather than letting latency build up.

## Development

```sh
dotnet build
dotnet test
dotnet run --project src/Reshare.Host -- --test-pattern -v
```

## Troubleshooting

- **"No virtual display driver found"**: install one (see above), or use `--display primary` to mirror.
- **Black screen / "Secure connection required"**: use the `https://` URL and accept the certificate.
- **Clicks don't reach some windows**: Windows blocks input into elevated (admin) windows from a non-elevated process; run `reshare-host` as Administrator.
- **Choppy video**: lower `--bitrate`/`--fps`, use 5 GHz Wi-Fi, and check F9 stats. `-v` on the host shows dropped frames.
