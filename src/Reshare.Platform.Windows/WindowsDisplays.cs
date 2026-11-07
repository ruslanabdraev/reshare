using System.Runtime.Versioning;
using Reshare.Core;
using static Reshare.Platform.Windows.Native;

namespace Reshare.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsDisplays
{
    /// <summary>Adapter names that indicate an indirect/virtual display driver.</summary>
    private static readonly string[] VirtualMarkers =
        ["virtual display", "iddsample", "idd ", "mtt", "parsec virtual", "usbmmidd", "spacedesk", "virtual monitor", "dummy"];

    public static bool IsVirtualAdapter(string adapterName) =>
        VirtualMarkers.Any(m => adapterName.Contains(m, StringComparison.OrdinalIgnoreCase));

    public record AdapterEntry(string DeviceName, string AdapterName, bool Attached, bool Primary);

    public static List<AdapterEntry> EnumerateAdapters()
    {
        var list = new List<AdapterEntry>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            list.Add(new AdapterEntry(dd.DeviceName, dd.DeviceString,
                (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0,
                (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
        }
        return list;
    }

    public static List<DisplayInfo> List()
    {
        var result = new List<DisplayInfo>();
        foreach (var a in EnumerateAdapters().Where(a => a.Attached))
        {
            var dm = DEVMODE.Create();
            if (!EnumDisplaySettingsExW(a.DeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0)) continue;
            string monitor = MonitorName(a.DeviceName);
            string name = monitor.Length > 0 && monitor != a.AdapterName ? $"{a.AdapterName} / {monitor}" : a.AdapterName;
            result.Add(new DisplayInfo(a.DeviceName, name,
                new Rect(dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight),
                a.Primary, IsVirtualAdapter(a.AdapterName)));
        }
        return result;
    }

    private static string MonitorName(string adapterDevice)
    {
        var dd = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevicesW(adapterDevice, 0, ref dd, 0) ? dd.DeviceString : "";
    }

    public static Rect VirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Switch a display to the requested mode if the driver offers it. Returns true if changed.</summary>
    public static bool TrySetMode(string device, int width, int height, int fps)
    {
        var cur = DEVMODE.Create();
        if (!EnumDisplaySettingsExW(device, ENUM_CURRENT_SETTINGS, ref cur, 0)) return false;
        if (cur.dmPelsWidth == width && cur.dmPelsHeight == height) return false;

        DEVMODE? best = null;
        for (int i = 0; ; i++)
        {
            var m = DEVMODE.Create();
            if (!EnumDisplaySettingsExW(device, i, ref m, 0)) break;
            if (m.dmPelsWidth != width || m.dmPelsHeight != height || m.dmBitsPerPel != 32) continue;
            // prefer the refresh rate closest to (but not below) the requested fps
            if (best == null || Score(m.dmDisplayFrequency) < Score(best.Value.dmDisplayFrequency)) best = m;
        }
        if (best == null)
        {
            Log.Warn($"{device} has no {width}x{height} mode; keeping {cur.dmPelsWidth}x{cur.dmPelsHeight}. " +
                     "Add the resolution in the virtual display driver's settings.");
            return false;
        }
        var mode = best.Value;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        int r = ChangeDisplaySettingsExW(device, ref mode, 0, 0, 0);
        if (r != DISP_CHANGE_SUCCESSFUL) { Log.Warn($"Changing {device} to {width}x{height} failed (code {r})"); return false; }
        Log.Info($"{device} set to {width}x{height}@{mode.dmDisplayFrequency}Hz");
        return true;

        int Score(uint hz) => hz >= fps ? (int)hz - fps : 1000 + fps - (int)hz;
    }

    /// <summary>Restore a display's mode from the registry (undoes a dynamic TrySetMode).</summary>
    public static void RestoreMode(string device) => ChangeDisplaySettingsExW(device, 0, 0, 0, 0);

    /// <summary>Attach a detached adapter to the desktop, to the right of everything else.</summary>
    public static bool TryAttach(string device, int width, int height)
    {
        var vs = VirtualScreen();
        var dm = DEVMODE.Create();
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        dm.dmPositionX = vs.Right;
        dm.dmPositionY = 0;
        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        int r = ChangeDisplaySettingsExW(device, ref dm, 0, CDS_UPDATEREGISTRY | CDS_NORESET, 0);
        if (r != DISP_CHANGE_SUCCESSFUL) return false;
        return ChangeDisplaySettingsExW(null, 0, 0, 0, 0) == DISP_CHANGE_SUCCESSFUL;
    }
}
