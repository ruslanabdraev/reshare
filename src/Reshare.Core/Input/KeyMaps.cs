namespace Reshare.Core.Input;

/// <summary>DOM KeyboardEvent.code -> native key identifiers for each host platform.</summary>
public static class KeyMaps
{
    /// <summary>Windows virtual-key code and whether KEYEVENTF_EXTENDEDKEY must be set.</summary>
    public static readonly IReadOnlyDictionary<string, (ushort Vk, bool Extended)> Windows = BuildWindows();

    /// <summary>macOS CGKeyCode (kVK_* from HIToolbox/Events.h).</summary>
    public static readonly IReadOnlyDictionary<string, ushort> MacOS = BuildMac();

    /// <summary>X11 keysym names for XStringToKeysym.</summary>
    public static readonly IReadOnlyDictionary<string, string> X11 = BuildX11();

    private static Dictionary<string, (ushort, bool)> BuildWindows()
    {
        var m = new Dictionary<string, (ushort, bool)>();
        for (char c = 'A'; c <= 'Z'; c++) m[$"Key{c}"] = (c, false);
        for (int d = 0; d <= 9; d++) m[$"Digit{d}"] = ((ushort)('0' + d), false);
        for (int f = 1; f <= 24; f++) m[$"F{f}"] = ((ushort)(0x6F + f), false);
        for (int n = 0; n <= 9; n++) m[$"Numpad{n}"] = ((ushort)(0x60 + n), false);
        void A(string code, ushort vk, bool ext = false) => m[code] = (vk, ext);
        A("Enter", 0x0D); A("Escape", 0x1B); A("Backspace", 0x08); A("Tab", 0x09); A("Space", 0x20);
        A("Minus", 0xBD); A("Equal", 0xBB); A("BracketLeft", 0xDB); A("BracketRight", 0xDD); A("Backslash", 0xDC);
        A("Semicolon", 0xBA); A("Quote", 0xDE); A("Backquote", 0xC0); A("Comma", 0xBC); A("Period", 0xBE);
        A("Slash", 0xBF); A("IntlBackslash", 0xE2); A("CapsLock", 0x14);
        A("PrintScreen", 0x2C, true); A("ScrollLock", 0x91); A("Pause", 0x13);
        A("Insert", 0x2D, true); A("Home", 0x24, true); A("PageUp", 0x21, true);
        A("Delete", 0x2E, true); A("End", 0x23, true); A("PageDown", 0x22, true);
        A("ArrowRight", 0x27, true); A("ArrowLeft", 0x25, true); A("ArrowDown", 0x28, true); A("ArrowUp", 0x26, true);
        A("NumLock", 0x90, true); A("NumpadDivide", 0x6F, true); A("NumpadMultiply", 0x6A);
        A("NumpadSubtract", 0x6D); A("NumpadAdd", 0x6B); A("NumpadEnter", 0x0D, true); A("NumpadDecimal", 0x6E);
        A("ContextMenu", 0x5D, true);
        A("ShiftLeft", 0xA0); A("ShiftRight", 0xA1); A("ControlLeft", 0xA2); A("ControlRight", 0xA3, true);
        A("AltLeft", 0xA4); A("AltRight", 0xA5, true); A("MetaLeft", 0x5B, true); A("MetaRight", 0x5C, true);
        return m;
    }

    private static Dictionary<string, ushort> BuildMac()
    {
        var m = new Dictionary<string, ushort>
        {
            ["KeyA"] = 0x00, ["KeyS"] = 0x01, ["KeyD"] = 0x02, ["KeyF"] = 0x03, ["KeyH"] = 0x04, ["KeyG"] = 0x05,
            ["KeyZ"] = 0x06, ["KeyX"] = 0x07, ["KeyC"] = 0x08, ["KeyV"] = 0x09, ["KeyB"] = 0x0B, ["KeyQ"] = 0x0C,
            ["KeyW"] = 0x0D, ["KeyE"] = 0x0E, ["KeyR"] = 0x0F, ["KeyY"] = 0x10, ["KeyT"] = 0x11,
            ["Digit1"] = 0x12, ["Digit2"] = 0x13, ["Digit3"] = 0x14, ["Digit4"] = 0x15, ["Digit6"] = 0x16,
            ["Digit5"] = 0x17, ["Equal"] = 0x18, ["Digit9"] = 0x19, ["Digit7"] = 0x1A, ["Minus"] = 0x1B,
            ["Digit8"] = 0x1C, ["Digit0"] = 0x1D, ["BracketRight"] = 0x1E, ["KeyO"] = 0x1F, ["KeyU"] = 0x20,
            ["BracketLeft"] = 0x21, ["KeyI"] = 0x22, ["KeyP"] = 0x23, ["Enter"] = 0x24, ["KeyL"] = 0x25,
            ["KeyJ"] = 0x26, ["Quote"] = 0x27, ["KeyK"] = 0x28, ["Semicolon"] = 0x29, ["Backslash"] = 0x2A,
            ["Comma"] = 0x2B, ["Slash"] = 0x2C, ["KeyN"] = 0x2D, ["KeyM"] = 0x2E, ["Period"] = 0x2F,
            ["Tab"] = 0x30, ["Space"] = 0x31, ["Backquote"] = 0x32, ["Backspace"] = 0x33, ["Escape"] = 0x35,
            ["MetaRight"] = 0x36, ["MetaLeft"] = 0x37, ["ShiftLeft"] = 0x38, ["CapsLock"] = 0x39,
            ["AltLeft"] = 0x3A, ["ControlLeft"] = 0x3B, ["ShiftRight"] = 0x3C, ["AltRight"] = 0x3D,
            ["ControlRight"] = 0x3E, ["IntlBackslash"] = 0x0A,
            ["NumpadDecimal"] = 0x41, ["NumpadMultiply"] = 0x43, ["NumpadAdd"] = 0x45, ["NumLock"] = 0x47,
            ["NumpadDivide"] = 0x4B, ["NumpadEnter"] = 0x4C, ["NumpadSubtract"] = 0x4E, ["NumpadEqual"] = 0x51,
            ["Numpad0"] = 0x52, ["Numpad1"] = 0x53, ["Numpad2"] = 0x54, ["Numpad3"] = 0x55, ["Numpad4"] = 0x56,
            ["Numpad5"] = 0x57, ["Numpad6"] = 0x58, ["Numpad7"] = 0x59, ["Numpad8"] = 0x5B, ["Numpad9"] = 0x5C,
            ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76, ["F5"] = 0x60, ["F6"] = 0x61,
            ["F7"] = 0x62, ["F8"] = 0x64, ["F9"] = 0x65, ["F10"] = 0x6D, ["F11"] = 0x67, ["F12"] = 0x6F,
            ["F13"] = 0x69, ["F14"] = 0x6B, ["F15"] = 0x71, ["F16"] = 0x6A, ["F17"] = 0x40, ["F18"] = 0x4F,
            ["F19"] = 0x50, ["F20"] = 0x5A,
            ["Insert"] = 0x72, ["Home"] = 0x73, ["PageUp"] = 0x74, ["Delete"] = 0x75, ["End"] = 0x77,
            ["PageDown"] = 0x79, ["ArrowLeft"] = 0x7B, ["ArrowRight"] = 0x7C, ["ArrowDown"] = 0x7D, ["ArrowUp"] = 0x7E,
        };
        return m;
    }

    private static Dictionary<string, string> BuildX11()
    {
        var m = new Dictionary<string, string>();
        for (char c = 'A'; c <= 'Z'; c++) m[$"Key{c}"] = char.ToLowerInvariant(c).ToString();
        for (int d = 0; d <= 9; d++) m[$"Digit{d}"] = d.ToString();
        for (int f = 1; f <= 24; f++) m[$"F{f}"] = $"F{f}";
        for (int n = 0; n <= 9; n++) m[$"Numpad{n}"] = $"KP_{n}";
        void A(string code, string sym) => m[code] = sym;
        A("Enter", "Return"); A("Escape", "Escape"); A("Backspace", "BackSpace"); A("Tab", "Tab"); A("Space", "space");
        A("Minus", "minus"); A("Equal", "equal"); A("BracketLeft", "bracketleft"); A("BracketRight", "bracketright");
        A("Backslash", "backslash"); A("Semicolon", "semicolon"); A("Quote", "apostrophe"); A("Backquote", "grave");
        A("Comma", "comma"); A("Period", "period"); A("Slash", "slash"); A("IntlBackslash", "less");
        A("CapsLock", "Caps_Lock"); A("PrintScreen", "Print"); A("ScrollLock", "Scroll_Lock"); A("Pause", "Pause");
        A("Insert", "Insert"); A("Home", "Home"); A("PageUp", "Prior"); A("Delete", "Delete"); A("End", "End");
        A("PageDown", "Next"); A("ArrowRight", "Right"); A("ArrowLeft", "Left"); A("ArrowDown", "Down"); A("ArrowUp", "Up");
        A("NumLock", "Num_Lock"); A("NumpadDivide", "KP_Divide"); A("NumpadMultiply", "KP_Multiply");
        A("NumpadSubtract", "KP_Subtract"); A("NumpadAdd", "KP_Add"); A("NumpadEnter", "KP_Enter");
        A("NumpadDecimal", "KP_Decimal"); A("NumpadEqual", "KP_Equal"); A("ContextMenu", "Menu");
        A("ShiftLeft", "Shift_L"); A("ShiftRight", "Shift_R"); A("ControlLeft", "Control_L"); A("ControlRight", "Control_R");
        A("AltLeft", "Alt_L"); A("AltRight", "ISO_Level3_Shift"); A("MetaLeft", "Super_L"); A("MetaRight", "Super_R");
        return m;
    }
}
