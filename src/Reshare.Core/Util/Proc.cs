using System.Diagnostics;

namespace Reshare.Core.Util;

public static class Proc
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static Result Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var r = new Result(p.ExitCode, output, err.Result);
            if (!r.Ok) Log.Debug($"{exe} {string.Join(' ', args)} -> {r.ExitCode}: {r.Stderr.Trim()}");
            return r;
        }
        catch (Exception e)
        {
            Log.Debug($"{exe} failed to start: {e.Message}");
            return new Result(-1, "", e.Message);
        }
    }

    public static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
