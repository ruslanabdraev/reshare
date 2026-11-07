using System.Text.Json;

namespace Reshare.Core.Input;

/// <summary>
/// Viewer -> host messages on the /input WebSocket (JSON text):
///   {t:"mm",x,y}        pointer move, normalized 0..1
///   {t:"md"|"mu",b}     button down/up (DOM button index)
///   {t:"wh",dx,dy}      wheel in CSS pixels
///   {t:"kd"|"ku",code}  key down/up, DOM KeyboardEvent.code
///   {t:"release"}       release everything (viewer lost focus)
///   {t:"ping",ts}       answered with {t:"pong",ts}
/// </summary>
public static class InputProtocol
{
    /// <summary>Apply one message to the injector. Returns a reply to send back, if any.</summary>
    public static string? Handle(string json, IInputInjector injector)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var tEl)) return null;
        double D(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        switch (tEl.GetString())
        {
            case "mm":
                injector.MoveTo(D("x"), D("y"));
                break;
            case "md" or "mu":
                int b = (int)D("b");
                if (b is >= 0 and <= 4) injector.Button((MouseButton)b, tEl.GetString() == "md");
                break;
            case "wh":
                injector.Wheel(D("dx"), D("dy"));
                break;
            case "kd" or "ku":
                if (root.TryGetProperty("code", out var c) && c.GetString() is { Length: > 0 and < 32 } code)
                    injector.Key(code, tEl.GetString() == "kd");
                break;
            case "release":
                injector.ReleaseAll();
                break;
            case "ping":
                return JsonSerializer.Serialize(new { t = "pong", ts = D("ts") });
        }
        return null;
    }
}

/// <summary>Tracks pressed keys/buttons so they can be released when the viewer disconnects.</summary>
public abstract class InputInjectorBase : IInputInjector
{
    private readonly HashSet<string> _keys = new();
    private readonly HashSet<MouseButton> _buttons = new();
    private double _wheelX, _wheelY;

    protected abstract void DoMove(double nx, double ny);
    protected abstract void DoButton(MouseButton button, bool down);
    /// <summary>Wheel in platform "notch" units (120 = one notch on Windows). Positive y = scroll down.</summary>
    protected abstract void DoWheel(int notchUnitsX, int notchUnitsY);
    protected abstract void DoKey(string domCode, bool down);

    public void MoveTo(double nx, double ny) => DoMove(nx, ny);

    public void Button(MouseButton button, bool down)
    {
        if (down) _buttons.Add(button);
        else if (!_buttons.Remove(button)) return;
        DoButton(button, down);
    }

    /// <summary>Converts CSS pixels to wheel units: 100px (a typical wheel notch in browsers) = 120 units.</summary>
    public void Wheel(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        _wheelX += dx * 1.2;
        _wheelY += dy * 1.2;
        int ix = (int)_wheelX, iy = (int)_wheelY;
        _wheelX -= ix;
        _wheelY -= iy;
        if (ix != 0 || iy != 0) DoWheel(ix, iy);
    }

    public void Key(string domCode, bool down)
    {
        if (down) _keys.Add(domCode);
        else if (!_keys.Remove(domCode)) return;
        DoKey(domCode, down);
    }

    public void ReleaseAll()
    {
        foreach (var k in _keys.ToArray()) Key(k, false);
        foreach (var b in _buttons.ToArray()) Button(b, false);
    }

    public virtual void Dispose() => ReleaseAll();
}

/// <summary>Used for --test-pattern and --no-input: logs instead of injecting.</summary>
public sealed class LoggingInjector : InputInjectorBase
{
    protected override void DoMove(double nx, double ny) { }
    protected override void DoButton(MouseButton button, bool down) => Log.Debug($"input: {button} {(down ? "down" : "up")}");
    protected override void DoWheel(int x, int y) => Log.Debug($"input: wheel {x},{y}");
    protected override void DoKey(string domCode, bool down) => Log.Debug($"input: {domCode} {(down ? "down" : "up")}");
}
