using System.Runtime.Versioning;
using Reshare.Core;

namespace Reshare.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    public const string VddUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases";

    public WindowsPlatform()
    {
        // Physical pixel coordinates everywhere (capture, cursor, SendInput).
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }

    public string Name => "Windows";
    public IReadOnlyList<string> PreferredEncoders => ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
    public IReadOnlyList<DisplayInfo> ListDisplays() => WindowsDisplays.List();

    public ICaptureSource CreateCapture(DisplayInfo display, HostOptions options) => new DxgiCapture(display, options.Fps);
    public IInputInjector CreateInputInjector(DisplayInfo display) => new WindowsInputInjector(display);

    public async Task<IDisplayLease> AcquireDisplayAsync(HostOptions o, CancellationToken ct)
    {
        if (o.Display != "auto")
            return new NoopLease(Select(o.Display));

        string? enabledDevice = null;
        var display = WindowsDisplays.List().FirstOrDefault(d => d.IsVirtual);
        if (display == null)
        {
            // Virtual adapter present but not part of the desktop?
            var detached = WindowsDisplays.EnumerateAdapters().FirstOrDefault(a => !a.Attached && WindowsDisplays.IsVirtualAdapter(a.AdapterName));
            if (detached != null)
            {
                Log.Info($"Attaching {detached.AdapterName} ({detached.DeviceName}) to the desktop");
                WindowsDisplays.TryAttach(detached.DeviceName, o.Width, o.Height);
            }
            else
            {
                var dev = VddController.FindVirtualDisplayDevices();
                var disabled = dev.FirstOrDefault(d => d.Disabled);
                if (dev.Count == 0)
                    throw new ReshareException(
                        "No virtual display driver found. To extend the desktop, install the free Virtual Display Driver:\n" +
                        $"    {VddUrl}\n" +
                        "  (or any other indirect display driver, e.g. Parsec VDD), then run reshare-host again.\n" +
                        "  To just mirror an existing monitor instead, use --display primary (see --list-displays).");
                if (disabled != null)
                {
                    if (!VddController.IsAdmin())
                        throw new ReshareException(
                            $"'{disabled.FriendlyName}' is installed but disabled. Enable it in Device Manager " +
                            "(Display adapters) or run reshare-host as Administrator so it can enable it.");
                    Log.Info($"Enabling {disabled.FriendlyName}");
                    if (!VddController.SetEnabled(disabled.InstanceId, true))
                        throw new ReshareException($"Failed to enable {disabled.FriendlyName}");
                    enabledDevice = disabled.InstanceId;
                }
            }

            for (int i = 0; i < 40 && display == null; i++)
            {
                await Task.Delay(250, ct);
                display = WindowsDisplays.List().FirstOrDefault(d => d.IsVirtual);
            }
            if (display == null)
                throw new ReshareException(
                    "Virtual display driver is installed but no virtual monitor is active. " +
                    "Open Settings > System > Display and make sure the virtual monitor is set to 'Extend these displays', " +
                    "or check the driver's monitor count setting.");
        }

        bool modeChanged = WindowsDisplays.TrySetMode(display.Id, o.Width, o.Height, o.Fps);
        if (modeChanged)
        {
            await Task.Delay(500, ct);
            display = WindowsDisplays.List().FirstOrDefault(d => d.Id == display.Id) ?? display;
        }
        return new Lease(display, modeChanged, o.KeepDisplay ? null : enabledDevice);
    }

    private DisplayInfo Select(string sel)
    {
        var all = WindowsDisplays.List();
        if (all.Count == 0) throw new ReshareException("No displays found");
        if (sel == "primary") return all.FirstOrDefault(d => d.IsPrimary) ?? all[0];
        if (int.TryParse(sel, out int n))
            return n >= 1 && n <= all.Count ? all[n - 1] : throw new ReshareException($"Display {n} does not exist (1..{all.Count})");
        return all.FirstOrDefault(d => d.Id.Equals(sel, StringComparison.OrdinalIgnoreCase)
                                       || d.Id.EndsWith(sel, StringComparison.OrdinalIgnoreCase))
               ?? throw new ReshareException($"Display '{sel}' not found. Use --list-displays.");
    }

    private sealed class Lease(DisplayInfo display, bool restoreMode, string? disableDevice) : IDisplayLease
    {
        public DisplayInfo Display { get; } = display;

        public ValueTask DisposeAsync()
        {
            if (restoreMode) WindowsDisplays.RestoreMode(Display.Id);
            if (disableDevice != null)
            {
                Log.Info("Disabling virtual display");
                VddController.SetEnabled(disableDevice, false);
            }
            return ValueTask.CompletedTask;
        }
    }
}
