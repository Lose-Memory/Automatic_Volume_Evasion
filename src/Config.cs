using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoVolume;

public enum TargetAction
{
    None = 0,
    Duck = 1,
    Pause = 2,
}

/// <summary>音乐程序可以被压低也可以被暂停；视频程序只能被压低，并且本身就充当视频触发源。</summary>
public enum TargetCategory
{
    Music = 0,
    Video = 1,
}

public sealed class TriggerSettings
{
    public bool Enabled { get; set; } = true;
    public double ThresholdDb { get; set; } = -45;
    public int SustainMs { get; set; } = 250;
    public int HoldMs { get; set; } = 400;
}

public sealed class HotkeySpec
{
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public string Key { get; set; } = "F8";

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}

public sealed class TargetSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>One program may ship a 32-bit and a 64-bit executable; both names live on one row.</summary>
    public string[] Processes { get; set; } = [];

    public TargetCategory Category { get; set; } = TargetCategory.Music;
    public bool Enabled { get; set; } = true;

    public TargetAction OnMic { get; set; } = TargetAction.None;
    public double OnMicDuckTo { get; set; } = 0.2;

    public TargetAction OnVideo { get; set; } = TargetAction.None;
    public double OnVideoDuckTo { get; set; } = 0.2;

    /// <summary>Only music targets use this: the app's own pause hotkey, sent when "暂停播放" is chosen.</summary>
    public HotkeySpec PauseHotkey { get; set; } = new();

    public int AttackMs { get; set; } = 120;
    public int ReleaseMs { get; set; } = 400;
}

public sealed class WindowPlacement
{
    public double Width { get; set; } = 1020;
    public double Height { get; set; } = 720;
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public bool HasPosition { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 3;

    public TriggerSettings Mic { get; set; } = new() { ThresholdDb = -45, SustainMs = 200, HoldMs = 400 };
    public TriggerSettings Video { get; set; } = new() { ThresholdDb = -50, SustainMs = 300, HoldMs = 400 };

    /// <summary>How often capture sessions are rescanned. Mic occupancy only shows up on a scan.</summary>
    public int ScanMsCapture { get; set; } = 200;

    /// <summary>System processes that keep a capture session open but are not the user talking. Always ignored.</summary>
    public static readonly string[] SystemIgnoredProcesses =
    [
        "svchost", "audiodg", "system", "idle", "registry", "csrss", "winlogon",
        "smss", "services", "lsass", "dwm", "explorer", "shellexperiencehost",
    ];

    /// <summary>Extra processes the user chose to ignore. Shown in the settings window.</summary>
    public string[] IgnoredCaptureProcesses { get; set; } = [];

    public bool RunAtStartup { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>How often the window redraws the level bars, in milliseconds.</summary>
    public int RefreshMs { get; set; } = 1000;

    /// <summary>Window colour scheme: "Light" or "Dark".</summary>
    public string Theme { get; set; } = "Light";

    /// <summary>Window backdrop: "None", "Mica" or "Acrylic". Needs Windows 11 22H2 or newer.</summary>
    public string WindowMaterial { get; set; } = "Mica";

    /// <summary>Last window size and position.</summary>
    public WindowPlacement Window { get; set; } = new();

    /// <summary>Last volume the user chose per app (process name). Used to undo a duck left behind by a crash.</summary>
    public Dictionary<string, float> AppVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Music targets plus video targets; <see cref="TargetSettings.Category"/> tells them apart.</summary>
    public List<TargetSettings> Targets { get; set; } = [];

    public static AppSettings CreateDefault() => new()
    {
        Targets =
        [
            new TargetSettings
            {
                Id = "kugou", Name = "酷狗音乐", Category = TargetCategory.Music,
                Processes = ["KuGou"],
                OnMic = TargetAction.Duck, OnMicDuckTo = 0.2,
                OnVideo = TargetAction.Duck, OnVideoDuckTo = 0.2,
                PauseHotkey = new HotkeySpec { Ctrl = true, Shift = true, Alt = true, Key = "F8" },
            },
            new TargetSettings
            {
                Id = "potplayer", Name = "PotPlayer", Category = TargetCategory.Video,
                Processes = ["PotPlayerMini64", "PotPlayerMini"],
                OnMic = TargetAction.Duck, OnMicDuckTo = 0.3,
            },
            new TargetSettings
            {
                Id = "browser", Name = "浏览器（Edge / Chrome）", Category = TargetCategory.Video,
                Processes = ["msedge", "chrome"],
                OnMic = TargetAction.Duck, OnMicDuckTo = 0.3,
            },
        ],
    };

    public void Normalize()
    {
        Migrate();

        Mic ??= new TriggerSettings();
        Video ??= new TriggerSettings();
        Targets ??= [];
        IgnoredCaptureProcesses = CleanProcessNames(IgnoredCaptureProcesses);
        AppVolumes = new Dictionary<string, float>(AppVolumes ?? new(), StringComparer.OrdinalIgnoreCase);

        foreach (var t in Targets)
        {
            t.Processes = CleanProcessNames(t.Processes);
            t.PauseHotkey ??= new HotkeySpec { Ctrl = true, Shift = true, Alt = true, Key = "F8" };
            t.OnMicDuckTo = Math.Clamp(t.OnMicDuckTo, 0, 1);
            t.OnVideoDuckTo = Math.Clamp(t.OnVideoDuckTo, 0, 1);
            t.AttackMs = Math.Clamp(t.AttackMs, 0, 5000);
            t.ReleaseMs = Math.Clamp(t.ReleaseMs, 0, 10000);

            // Video targets only ever duck, and never duck themselves through the video trigger.
            if (t.Category == TargetCategory.Video)
            {
                t.OnMic = t.OnMic == TargetAction.Pause ? TargetAction.Duck : t.OnMic;
                t.OnVideo = TargetAction.None;
            }
        }

        Targets = Targets
            .Where(t => t.Processes.Length > 0)
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        Mic.ThresholdDb = Math.Clamp(Mic.ThresholdDb, -90, 0);
        Video.ThresholdDb = Math.Clamp(Video.ThresholdDb, -90, 0);
        ScanMsCapture = Math.Clamp(ScanMsCapture, 50, 5000);
        RefreshMs = Math.Clamp(RefreshMs, 100, 5000);
        Theme = string.Equals(Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        WindowMaterial = WindowMaterial switch
        {
            "Acrylic" => "Acrylic",
            "Mica" => "Mica",
            _ => "None",
        };
        Window ??= new WindowPlacement();
        Window.Width = Math.Clamp(Window.Width, 600, 8000);
        Window.Height = Math.Clamp(Window.Height, 400, 8000);
    }

    /// <summary>
    /// v3：目标分成音乐 / 视频两类，视频类同时充当触发源；暂停改为每个音乐程序自带热键。
    /// 旧的三条固定目标按 Id 归类，未知目标一律归视频（只压低、不暂停，行为更保守）。
    /// </summary>
    private void Migrate()
    {
        if (Version >= 3) return;

        var oldHotkey = KuGouPauseHotkey ?? new HotkeySpec { Ctrl = true, Shift = true, Alt = true, Key = "F8" };

        foreach (var t in Targets ?? [])
        {
            var music = string.Equals(t.Id, "kugou", StringComparison.OrdinalIgnoreCase);
            t.Category = music ? TargetCategory.Music : TargetCategory.Video;
            t.PauseHotkey = oldHotkey;

            if (!music) t.OnVideo = TargetAction.None;
        }

        KuGouPauseHotkey = null;
        Version = 3;
    }

    /// <summary>Legacy v2 field: the single global pause hotkey. Only read while migrating.</summary>
    public HotkeySpec? KuGouPauseHotkey { get; set; }

    /// <summary>Trims entries, drops a trailing ".exe" (Task Manager shows that form), and removes duplicates.</summary>
    private static string[] CleanProcessNames(string[]? names) =>
        (names ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>settings.json next to the executable. Debounced atomic writes, written on exit.</summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private readonly System.Threading.Timer _timer;
    private bool _pending;

    public string FilePath { get; }
    public AppSettings Settings { get; private set; }

    public SettingsStore()
    {
        FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        Settings = Load(FilePath);
        _timer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SaveSoon()
    {
        lock (_lock) _pending = true;
        try { _timer.Change(400, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    public void Flush()
    {
        string json;
        lock (_lock)
        {
            if (!_pending) return;
            _pending = false;
            Settings.Normalize();
            json = JsonSerializer.Serialize(Settings, JsonOptions);
        }

        try
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"保存配置失败: {ex.Message}");
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"读取配置失败，使用默认值: {ex.Message}");
            try { File.Move(path, path + ".bad", overwrite: true); } catch { }
        }

        var defaults = AppSettings.CreateDefault();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write($"创建默认配置失败: {ex.Message}");
        }

        return defaults;
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock) _pending = true;
        Flush();
    }
}