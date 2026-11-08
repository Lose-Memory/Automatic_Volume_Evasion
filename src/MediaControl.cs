using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Media.Control;

namespace AutoVolume;

public sealed class MediaSessionInfo
{
    public required string Aumid { get; init; }
    public required string Title { get; init; }
    public required bool Playing { get; init; }
    public GlobalSystemMediaTransportControlsSession? Session { get; init; }
}

/// <summary>
/// Pause/resume through Windows media sessions (GSMTC) and, for apps that don't register one
/// (KuGou), through a global hotkey that the app itself registered.
/// </summary>
public static class MediaControl
{
    private static readonly object Gate = new();
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static DateTime _managerFailedAt = DateTime.MinValue;

    public static List<MediaSessionInfo> GetSessions()
    {
        var result = new List<MediaSessionInfo>();
        try
        {
            var manager = GetManager();
            if (manager == null) return result;

            foreach (var session in manager.GetSessions())
            {
                string aumid, title = "";
                bool playing;
                try
                {
                    aumid = session.SourceAppUserModelId ?? "";
                    playing = session.GetPlaybackInfo().PlaybackStatus ==
                              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    try
                    {
                        var props = session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                        title = props?.Title ?? "";
                    }
                    catch { }
                }
                catch
                {
                    continue;
                }

                result.Add(new MediaSessionInfo { Aumid = aumid, Title = title, Playing = playing, Session = session });
            }
        }
        catch (Exception ex)
        {
            Log.Write($"读取媒体会话失败: {ex.Message}");
        }

        return result;
    }

    public static bool Pause(MediaSessionInfo info) => Run(info, pause: true);
    public static bool Play(MediaSessionInfo info) => Run(info, pause: false);

    private static bool Run(MediaSessionInfo info, bool pause)
    {
        if (info.Session == null) return false;
        try
        {
            var op = pause ? info.Session.TryPauseAsync() : info.Session.TryPlayAsync();
            return op.AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Write($"媒体会话{(pause ? "暂停" : "恢复")}失败 ({info.Aumid}): {ex.Message}");
            return false;
        }
    }

    private static GlobalSystemMediaTransportControlsSessionManager? GetManager()
    {
        lock (Gate)
        {
            if (_manager != null) return _manager;
            if (DateTime.UtcNow - _managerFailedAt < TimeSpan.FromSeconds(5)) return null;

            try
            {
                _manager = GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _managerFailedAt = DateTime.UtcNow;
                Log.Write($"初始化媒体会话管理器失败: {ex.Message}");
                _manager = null;
            }

            return _manager;
        }
    }

    // ---- global hotkey (KuGou) ----

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