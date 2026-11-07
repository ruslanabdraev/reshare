using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Reshare.Core;

namespace Reshare.Platform.Windows;

/// <summary>
/// Finds and enables/disables an installed indirect display driver (e.g. the open-source
/// Virtual Display Driver, https://github.com/VirtualDrivers/Virtual-Display-Driver) through PnP.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class VddController
{
    public record PnpDevice(string InstanceId, string FriendlyName, string Status, int ProblemCode)
    {
        public bool Disabled => ProblemCode == 22; // CM_PROB_DISABLED
    }

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Virtual display adapters known to PnP, including disabled ones.</summary>
    public static List<PnpDevice> FindVirtualDisplayDevices()
    {
        const string script =
            "Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | " +
            "Select-Object InstanceId,FriendlyName,Status,ConfigManagerErrorCode | ConvertTo-Json -Compress";
        string json = Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], out _);
        var list = new List<PnpDevice>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            foreach (var e in items)
            {
                string name = e.TryGetProperty("FriendlyName", out var n) ? n.GetString() ?? "" : "";
                if (!WindowsDisplays.IsVirtualAdapter(name)) continue;
                int code = e.TryGetProperty("ConfigManagerErrorCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                list.Add(new PnpDevice(e.GetProperty("InstanceId").GetString() ?? "", name,
                    e.TryGetProperty("Status", out var s) ? s.GetString() ?? "" : "", code));
            }
        }
        catch (JsonException ex) { Log.Debug($"Get-PnpDevice output not understood: {ex.Message}"); }
        return list;
    }

    public static bool SetEnabled(string instanceId, bool enabled)
    {
        Run("pnputil.exe", [enabled ? "/enable-device" : "/disable-device", instanceId], out int code);
        return code == 0;
    }

    private static string Run(string exe, string[] args, out int exitCode)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);
            exitCode = p.ExitCode;
            if (exitCode != 0) Log.Debug($"{exe} exited {exitCode}: {err.Result.Trim()} {output.Trim()}");
            return output;
        }
        catch (Exception e)
        {
            Log.Debug($"{exe} failed: {e.Message}");
            exitCode = -1;
            return "";
        }
    }
}
