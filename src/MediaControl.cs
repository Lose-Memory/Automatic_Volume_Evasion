using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoVolume;

/// <summary>
/// Pause/resume by replaying the player's own hotkey: most music players don't expose a media
/// session that the system can drive, and per-program hotkeys are what the settings model uses.
/// </summary>
public static class MediaControl
{
    private const uint KeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static bool SendHotkey(HotkeySpec spec)
    {
        if (!TryGetVirtualKey(spec.Key, out var vk))
        {
            Log.Write($"无法识别的热键按键: {spec.Key}");
            return false;
        }

        var modifiers = new List<byte>();
        if (spec.Ctrl) modifiers.Add(0x11);
        if (spec.Shift) modifiers.Add(0x10);
        if (spec.Alt) modifiers.Add(0x12);

        foreach (var m in modifiers)
            keybd_event(m, 0, 0, UIntPtr.Zero);

        Thread.Sleep(25);
        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(25);
        keybd_event((byte)vk, 0, KeyUp, UIntPtr.Zero);
        Thread.Sleep(25);

        for (var i = modifiers.Count - 1; i >= 0; i--)
            keybd_event(modifiers[i], 0, KeyUp, UIntPtr.Zero);

        return true;
    }

    private static bool TryGetVirtualKey(string name, out int vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var text = name.Trim();
        if (text.Length == 1)
        {
            var c = char.ToUpperInvariant(text[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
        }

        if (text.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = 0x6F + fn; // VK_F1 = 0x70
            return true;
        }

        if (Enum.TryParse<Keys>(text, ignoreCase: true, out var keys))
        {
            vk = (int)keys;
            return vk > 0;
        }

        return false;
    }
}