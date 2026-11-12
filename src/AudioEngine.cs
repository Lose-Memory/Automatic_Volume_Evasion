using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace AutoVolume;

public sealed class AppSnapshot
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public float Level { get; init; }
    public float Volume { get; init; }
    public float Gain { get; init; } = 1f;
    public string State { get; init; } = "";
}

public sealed class EngineSnapshot
{
    public bool MicInUse { get; init; }
    public string? MicOwner { get; init; }
    public bool MicActive { get; init; }
    public float VideoLevel { get; init; }
    public bool VideoActive { get; init; }
    public bool Enabled { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<AppSnapshot> Apps { get; init; } = [];
    public IReadOnlyDictionary<string, string> TargetStates { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, float> TargetVolumes { get; init; } = new Dictionary<string, float>();
}

/// <summary>
/// Owns every WASAPI object on one dedicated thread. Every 50 ms it reads levels, evaluates the
/// mic/video triggers, and either ducks a target's session volume or pauses it.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int TickMs = 50;
    private const float SilenceDb = -100f;

    private readonly SettingsStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, AppEntry> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _processNames = new();
    private readonly Dictionary<string, TargetRuntime> _runtime = new(StringComparer.OrdinalIgnoreCase);

    private Thread? _thread;
    private volatile bool _running;
    private volatile EngineSnapshot _snapshot = new();
    private string? _lastError;

    private MMDeviceEnumerator? _enumerator;
    private List<MMDevice> _renderDevices = [];
    private MMDevice? _defaultCapture;
    private List<MMDevice> _captureDevices = [];
    private string? _micOwner;

    private DateTime _lastSessionScan = DateTime.MinValue;
    private DateTime _lastCaptureScan = DateTime.MinValue;
    private DateTime _lastDeviceScan = DateTime.MinValue;
    private bool _deviceScanLogged;

    /// <summary>Benchmark mode: evaluate triggers but never touch any volume.</summary>
    public bool DryRun { get; set; }

    
    private long _scanCount, _captureCount;
    private double _scanTotalMs, _scanMaxMs, _captureTotalMs, _captureMaxMs;

    public string ScanStats()
    {
        static string Fmt(long n, double total, double max) => n == 0
            ? "无"
            : $"{n} 次, 平均 {total / n * 1000:0.0} µs, 最大 {max * 1000:0.0} µs, 合计 {total:0.0} ms";
        return $"输出会话扫描: {Fmt(_scanCount, _scanTotalMs, _scanMaxMs)}；" +
               $"采集会话扫描: {Fmt(_captureCount, _captureTotalMs, _captureMaxMs)}";
    }

    private readonly TriggerState _micState = new();
    private readonly TriggerState _videoState = new();

    
    private int _testCommand;
    private DateTime _testPauseUntil = DateTime.MinValue;

    /// <summary>0 = none, 1 = pause every target for 5 s, 2 = resume every target now.</summary>
    public int TestCommand
    {
        get => Volatile.Read(ref _testCommand);
        set => Volatile.Write(ref _testCommand, value);
    }

    public AudioEngine(SettingsStore store)
    {
        _store = store;
    }

    public EngineSnapshot Snapshot => _snapshot;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioEngine" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(3000);
        _thread = null;
        ResetDevices();
    }

    private void Run()
    {
        Log.Write("音频引擎启动");
        var last = DateTime.UtcNow;

        while (_running)
        {
            var now = DateTime.UtcNow;
            var elapsedMs = (float)Math.Clamp((now - last).TotalMilliseconds, 1, 500);
            last = now;

            try
            {
                Tick(now, elapsedMs);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.Write($"引擎循环异常: {ex}");
                ResetDevices();
            }

            Thread.Sleep(TickMs);
        }

        RestoreVolumes();
        Log.Write("音频引擎停止");
    }

    private void Tick(DateTime now, float elapsedMs)
    {
        var settings = _store.Settings;

        EnsureDevices(now);
        // Output sessions are expensive to enumerate and a video signal tolerates a second of lag.
        if ((now - _lastSessionScan).TotalMilliseconds >= 1000)
        {
            var sw = DryRun ? System.Diagnostics.Stopwatch.StartNew() : null;

            ScanSessions();
            if (sw != null)
            {
                sw.Stop();
                _scanCount++;
                _scanTotalMs += sw.Elapsed.TotalMilliseconds;
                _scanMaxMs = Math.Max(_scanMaxMs, sw.Elapsed.TotalMilliseconds);
            }

            _lastSessionScan = now;
        }

        // Capture sessions are cheap to enumerate and the mic trigger is a binary signal, so this
        // runs at the user's configured rate (200 ms by default) to keep the reaction snappy.
        if ((now - _lastCaptureScan).TotalMilliseconds >= settings.ScanMsCapture)
        {
            var sw = DryRun ? System.Diagnostics.Stopwatch.StartNew() : null;

            ScanCaptureSessions(settings);
            if (sw != null)
            {
                sw.Stop();
                _captureCount++;
                _captureTotalMs += sw.Elapsed.TotalMilliseconds;
                _captureMaxMs = Math.Max(_captureMaxMs, sw.Elapsed.TotalMilliseconds);
            }

            _lastCaptureScan = now;
        }

        ReadLevels();

        var videoDb = LinearToDb(VideoLevel(settings));

        var micWasActive = _micState.Active;
        var videoWasActive = _videoState.Active;
        Evaluate(_micState, settings.Mic, _micOwner != null ? 0f : SilenceDb, now);
        Evaluate(_videoState, settings.Video, videoDb, now);
        if (micWasActive != _micState.Active)
            Log.Write($"麦克风触发: {(_micState.Active ? "开" : "关")}（占用: {_micOwner ?? "无"}）");
        if (videoWasActive != _videoState.Active) Log.Write($"视频触发: {(_videoState.Active ? "开" : "关")} ({videoDb:0} dB)");

        var command = Interlocked.Exchange(ref _testCommand, 0);
        if (command == 1)
        {
            _testPauseUntil = now.AddSeconds(5);
            Log.Write("测试：暂停全部音乐目标");

            foreach (var target in settings.Targets)
            {
                if (!target.Enabled || target.Category != TargetCategory.Music) continue;
                var runtime = GetRuntime(target.Id);
                var playing = IsPlaying(target);
                Log.Write($"  {target.Name}: IsPlaying={playing}");

                if (playing && Pause(target)) runtime.PausedByUs = true;
                runtime.State = "测试暂停";
            }
        }
        else if (command == 2)
        {
            _testPauseUntil = DateTime.MinValue;
            Log.Write("测试：恢复全部目标");
        }
        else if (command == 3)
        {
            ResetTargetVolumes(settings);
        }
        else if (command == 4)
        {
            DumpSessions();
        }

        if (now < _testPauseUntil)
        {
            Publish(settings, videoDb);
            return;
        }

        foreach (var target in settings.Targets)
        {
            var runtime = GetRuntime(target.Id);
            ApplyTarget(target, runtime, settings, videoDb, elapsedMs);
        }

        Publish(settings, videoDb);
    }

    // ---- devices & sessions ----

    private void EnsureDevices(DateTime now)
    {
        if (_enumerator == null)
        {
            _enumerator = new MMDeviceEnumerator();
            _lastDeviceScan = DateTime.MinValue;
        }

        if (now - _lastDeviceScan < TimeSpan.FromSeconds(30)) return;

        _lastDeviceScan = now;
        try
        {
            var devices = new List<MMDevice>();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                devices.Add(device);

            foreach (var old in _renderDevices) old.Dispose();
            _renderDevices = devices;

            _defaultCapture?.Dispose();
            _defaultCapture = _enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : null;

            foreach (var old in _captureDevices) old.Dispose();
            _captureDevices = [];
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                _captureDevices.Add(device);

            if (!_deviceScanLogged)
            {
                _deviceScanLogged = true;
                Log.Write($"设备扫描: 输出 {devices.Count} 个, 录音 {_captureDevices.Count} 个, " +
                          $"默认录音 = {_defaultCapture?.FriendlyName ?? "无"}");
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log.Write($"扫描音频设备失败: {ex.Message}");
        }
    }

    private void ResetDevices()
    {
        foreach (var device in _renderDevices)
        {
            try { device.Dispose(); } catch { }
        }
        _renderDevices = [];

        foreach (var device in _captureDevices)
        {
            try { device.Dispose(); } catch { }
        }
        _captureDevices = [];

        _defaultCapture?.Dispose();
        _defaultCapture = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _lastDeviceScan = DateTime.MinValue;
        _apps.Clear();
    }

    private void ScanSessions()
    {
        var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var livePids = new HashSet<int>();

        foreach (var device in _renderDevices)
        {
            SessionCollection sessions;
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                sessions = manager.Sessions;
            }
            catch
            {
                continue;
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                    var pid = (int)session.GetProcessID;
                    if (pid == 0 || session.IsSystemSoundsSession) continue;
                    if (pid == Environment.ProcessId) continue;

                    var name = ResolveProcessName(pid);
                    if (name == null) continue;

                    livePids.Add(pid);
                    var key = Normalize(name);
                    if (!found.TryGetValue(key, out var app))
                        found[key] = app = new AppEntry { Key = key, Name = name };

                    var instanceId = SafeInstanceId(session, pid, i);
                    app.Sessions.Add(new SessionRef(instanceId, session));
                }
                catch
                {
                    // Session vanished mid-scan.
                }
            }
        }

        foreach (var pid in _processNames.Keys.Where(p => !livePids.Contains(p)).ToList())
            _processNames.Remove(pid);

        // Keep existing SessionRef objects (they hold base volumes) when the instance is unchanged.
        foreach (var (key, scanned) in found)
        {
            if (!_apps.TryGetValue(key, out var existing))
            {
                _apps[key] = scanned;
                continue;
            }

            var previous = existing.Sessions.ToDictionary(s => s.InstanceId, StringComparer.Ordinal);
            existing.Name = scanned.Name;
            existing.Sessions = scanned.Sessions
                .Select(s => previous.TryGetValue(s.InstanceId, out var old) ? old : s)
                .ToList();
        }

        foreach (var key in _apps.Keys.Where(k => !found.ContainsKey(k)).ToList())
            _apps.Remove(key);
    }

    private string? ResolveProcessName(int pid)
    {
        if (_processNames.TryGetValue(pid, out var cached)) return cached;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var name = process.ProcessName;
            _processNames[pid] = name;
            return name;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeInstanceId(AudioSessionControl session, int pid, int index)
    {
        try { return session.GetSessionInstanceIdentifier; }
        catch { return $"{pid}|{index}"; }
    }

    private static string Normalize(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool IsIgnoredCapture(string processName, string[] userIgnored)
    {
        var key = Normalize(processName.Trim());
        return MatchesAny(key, AppSettings.SystemIgnoredProcesses) || MatchesAny(key, userIgnored);
    }

    private static bool MatchesAny(string key, string[] names)
    {
        foreach (var entry in names)
        {
            if (string.Equals(Normalize(entry.Trim()), key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---- levels ----

    private void ReadLevels()
    {
        foreach (var app in _apps.Values)
        {
            var level = 0f;
            var active = false;
            foreach (var session in app.Sessions)
            {
                try
                {
                    level = MathF.Max(level, session.Control.AudioMeterInformation.MasterPeakValue);
                    if (session.Control.State == AudioSessionState.AudioSessionStateActive) active = true;
                }
                catch { }
            }
            app.Level = level;
            app.AnyActive = active;
        }
    }

    /// <summary>Video targets double as the trigger source: any of them producing audio counts as playback.</summary>
    private float VideoLevel(AppSettings settings)
    {
        var level = 0f;
        foreach (var target in settings.Targets)
        {
            if (target.Category != TargetCategory.Video) continue;
            foreach (var process in target.Processes)
            {
                if (_apps.TryGetValue(Normalize(process), out var app))
                    level = MathF.Max(level, app.Level);
            }
        }
        return level;
    }

    // ---- mic ----

    /// <summary>
    /// Detects microphone occupancy instead of level: a process that opens an active capture
    /// session on any recording device counts as "using the mic" (voice input, calls, recording).
    /// </summary>
    private void ScanCaptureSessions(AppSettings settings)
    {
        string? owner = null;

        foreach (var device in _captureDevices)
        {
            SessionCollection sessions;
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                sessions = manager.Sessions;
            }
            catch
            {
                continue;
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session.IsSystemSoundsSession) continue;

                    var pid = (int)session.GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId) continue;
                    if (session.State != AudioSessionState.AudioSessionStateActive) continue;

                    var name = ResolveProcessName(pid);
                    if (name == null) continue;
                    if (IsIgnoredCapture(name, settings.IgnoredCaptureProcesses)) continue;

                    owner = name;
                    break;
                }
                catch
                {
                    // Session vanished mid-scan.
                }
            }

            if (owner != null) break;
        }

        if (owner == _micOwner) return;

        _micOwner = owner;
        Log.Write(owner == null ? "麦克风空闲" : $"麦克风被占用: {owner}");
    }

    // ---- triggers ----

    private sealed class TriggerState
    {
        public bool Active;
        public DateTime AboveSince = DateTime.MinValue;
        public DateTime LastAbove = DateTime.MinValue;
    }

    private static void Evaluate(TriggerState state, TriggerSettings cfg, float levelDb, DateTime now)
    {
        if (!cfg.Enabled)
        {
            state.Active = false;
            state.AboveSince = DateTime.MinValue;
            return;
        }

        if (levelDb >= cfg.ThresholdDb)
        {
            if (state.AboveSince == DateTime.MinValue) state.AboveSince = now;
            state.LastAbove = now;
        }

        if (!state.Active)
        {
            if (state.AboveSince != DateTime.MinValue &&
                (now - state.AboveSince).TotalMilliseconds >= cfg.SustainMs)
                state.Active = true;
        }
        else if ((now - state.LastAbove).TotalMilliseconds >= cfg.HoldMs)
        {
            state.Active = false;
            state.AboveSince = DateTime.MinValue;
        }
    }

    // ---- targets ----

    private sealed class TargetRuntime
    {
        public float Gain = 1f;
        public bool PausedByUs;
        public string State = "";
        public string Coarse = "";
    }

    private TargetRuntime GetRuntime(string id)
    {
        if (!_runtime.TryGetValue(id, out var runtime))
            _runtime[id] = runtime = new TargetRuntime();
        return runtime;
    }

    private void ApplyTarget(TargetSettings target, TargetRuntime runtime, AppSettings settings,
        float videoDb, float elapsedMs)
    {
        if (DryRun) return;

        if (!settings.Enabled || !target.Enabled)
        {
            if (runtime.PausedByUs) Resume(target, runtime);
            runtime.Gain = 1f;
            runtime.State = "已禁用";
            ApplyVolume(target, 1f);
            return;
        }

        var wantPause = false;
        var duckTo = 1f;
        var ducking = false;

        if (_micState.Active && target.OnMic != TargetAction.None)
        {
            if (target.OnMic == TargetAction.Pause) wantPause = true;
            else { ducking = true; duckTo = MathF.Min(duckTo, (float)target.OnMicDuckTo); }
        }

        // 视频类目标就是触发源本身，视频播放不压低它们。
        if (target.Category == TargetCategory.Music && _videoState.Active && target.OnVideo != TargetAction.None)
        {
            if (target.OnVideo == TargetAction.Pause) wantPause = true;
            else { ducking = true; duckTo = MathF.Min(duckTo, (float)target.OnVideoDuckTo); }
        }

        if (wantPause)
        {
            runtime.Gain = 1f;
            var playing = IsPlaying(target);
            if (!runtime.PausedByUs && playing)
            {
                if (Pause(target)) runtime.PausedByUs = true;
            }
            runtime.State = runtime.PausedByUs ? "已暂停" : playing ? "暂停失败" : "等待播放";
            var pauseCoarse = runtime.PausedByUs ? "已暂停" : playing ? "暂停失败" : "等待播放";
            if (pauseCoarse != runtime.Coarse)
            {
                Log.Write($"{target.Name}: {pauseCoarse}");
                runtime.Coarse = pauseCoarse;
            }
            ApplyVolume(target, 1f);
            return;
        }

        if (runtime.PausedByUs) Resume(target, runtime);

        var goal = ducking ? Math.Clamp(duckTo, 0f, 1f) : 1f;
        var timeMs = goal < runtime.Gain ? target.AttackMs : target.ReleaseMs;
        var coefficient = timeMs <= 0 ? 1f : 1f - MathF.Exp(-elapsedMs / timeMs);
        runtime.Gain += (goal - runtime.Gain) * coefficient;
        if (MathF.Abs(runtime.Gain - goal) < 0.002f) runtime.Gain = goal;

        runtime.State = runtime.Gain < 0.999f
            ? $"压低到 {runtime.Gain * 100:0}%"
            : (ducking ? "压低中" : "正常");

        ApplyVolume(target, runtime.Gain);

        var coarse = runtime.Gain < 0.999f ? "压低" : "正常";
        if (coarse != runtime.Coarse)
        {
            Log.Write($"{target.Name}: {coarse}{DescribeVolume(target)}");
            runtime.Coarse = coarse;
        }
    }

    private void DumpSessions()
    {
        Log.Write("=== 录音设备 ===");
        try
        {
            if (_enumerator != null)
            {
                var defaultId = _defaultCapture?.ID ?? "";
                foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    using (device)
                        Log.Write($"  {(device.ID == defaultId ? "[默认] " : "       ")}{device.FriendlyName}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"  枚举录音设备失败: {ex.Message}");
        }

        Log.Write("=== 录音会话 ===");
        foreach (var device in _captureDevices)
        {
            try
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var session = sessions[i];
                        var pid = (int)session.GetProcessID;
                        var name = pid == 0 ? "system" : ResolveProcessName(pid) ?? $"PID {pid}";
                        Log.Write($"  [{device.FriendlyName}] {name}: {session.State}");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"  [{device.FriendlyName}] 枚举失败: {ex.Message}");
            }
        }

        Log.Write("=== 会话转储 ===");
        foreach (var app in _apps.Values.OrderBy(a => a.Key))
        {
            foreach (var session in app.Sessions)
            {
                try
                {
                    Log.Write($"  {app.Name}: state={session.Control.State}, " +
                              $"peak={LinearToDb(session.Control.AudioMeterInformation.MasterPeakValue):0} dB, " +
                              $"volume={session.Control.SimpleAudioVolume.Volume * 100:0}%");
                }
                catch (Exception ex)
                {
                    Log.Write($"  {app.Name}: 读取失败 {ex.Message}");
                }
            }
        }
        Log.Write("=== 转储结束 ===");
    }

    private void ResetTargetVolumes(AppSettings settings)
    {
        foreach (var target in settings.Targets)
        {
            foreach (var process in target.Processes)
            {
                var key = Normalize(process);
                if (!_apps.TryGetValue(key, out var app)) continue;

                foreach (var session in app.Sessions)
                {
                    try
                    {
                        session.Control.SimpleAudioVolume.Volume = 1f;
                        session.Base = 1f;
                        session.Applied = 1f;
                    }
                    catch { }
                }

                SaveVolume(key, 1f);
            }
        }

        Log.Write("已把目标应用音量重置为 100%");
    }

    private void SaveVolume(string key, float volume)
    {
        _store.Settings.AppVolumes[key] = Math.Clamp(volume, 0f, 1f);
        _store.SaveSoon();
    }

    private void RestoreVolumes()
    {
        foreach (var app in _apps.Values)
        {
            foreach (var session in app.Sessions)
            {
                try
                {
                    if (session.Base >= 0f)
                        session.Control.SimpleAudioVolume.Volume = session.Base;
                }
                catch { }
            }
        }
    }

    private string DescribeVolume(TargetSettings target)
{
        foreach (var process in target.Processes)
        {
            if (!_apps.TryGetValue(Normalize(process), out var app) || app.Sessions.Count == 0) continue;
            try
            {
                var volume = app.Sessions[0].Control.SimpleAudioVolume.Volume;
                return $"（当前音量 {volume * 100:0}%）";
            }
            catch { }
        }
        return "（未找到会话）";
    }

    private void ApplyVolume(TargetSettings target, float gain)
    {
        foreach (var process in target.Processes)
        {
            var key = Normalize(process);
            if (!_apps.TryGetValue(key, out var app)) continue;

            foreach (var session in app.Sessions)
            {
                try
                {
                    var control = session.Control;
                    var volume = control.SimpleAudioVolume.Volume;

                    if (session.Base < 0f)
                    {
                        if (_store.Settings.AppVolumes.TryGetValue(key, out var saved) && saved > 0.001f)
                        {
                            // Restore the user's level so a duck left behind by a crash does not stick.
                            session.Base = saved;
                            session.Applied = saved;
                            if (MathF.Abs(volume - saved) > 0.005f)
                            {
                                control.SimpleAudioVolume.Volume = saved;
                                volume = saved;
                            }
                        }
                        else
                        {
                            session.Base = volume;
                            session.Applied = volume;
                            SaveVolume(key, volume);
                        }
                    }

                    if (gain > 0.999f)
                    {
                        if (session.Applied >= 0f && MathF.Abs(volume - session.Applied) > 0.02f)
                        {
                            // 用户自己拧了音量条，把它当成新的基准
                            session.Base = volume;
                            SaveVolume(key, volume);
                        }
                        else if (MathF.Abs(volume - session.Base) > 0.0005f)
                        {
                            // 回升过程写下的最后一格是 99.x%，要显式补回用户原来的音量
                            control.SimpleAudioVolume.Volume = session.Base;
                            volume = session.Base;
                        }

                        session.Applied = volume;
                    }
                    else
                    {
                        if (session.Applied >= 0f && MathF.Abs(volume - session.Applied) > 0.02f)
                            session.Base = Math.Clamp(volume / gain, 0f, 1f);

                        var effective = Math.Clamp(session.Base * gain, 0f, 1f);
                        if (MathF.Abs(volume - effective) > 0.002f)
                            control.SimpleAudioVolume.Volume = effective;
                        session.Applied = effective;
                    }
                }
                catch
                {
                    // Session went away.
                }
            }
        }
    }

    // ---- pause / resume ----

    /// <summary>A player keeps its session "active" for a while after pausing, so trust the meter.</summary>
    private bool IsPlaying(TargetSettings target)
    {
        foreach (var process in target.Processes)
        {
            if (_apps.TryGetValue(Normalize(process), out var app) && app.Level > 0.003f)
                return true;
        }
        return false;
    }

    private bool Pause(TargetSettings target)
    {
        Log.Write($"{target.Name}: 发送暂停热键 {target.PauseHotkey}");
        return MediaControl.SendHotkey(target.PauseHotkey);
    }

    private void Resume(TargetSettings target, TargetRuntime runtime)
    {
        runtime.PausedByUs = false;

        // The hotkey is a toggle: only send it while the app is actually paused.
        if (IsPlaying(target))
        {
            Log.Write($"{target.Name}: 已在播放，跳过恢复");
            return;
        }

        Log.Write($"{target.Name}: 发送恢复热键");
        MediaControl.SendHotkey(target.PauseHotkey);
    }

    // ---- snapshot ----

    private void Publish(AppSettings settings, float videoDb)
    {
        var apps = new List<AppSnapshot>();
        foreach (var app in _apps.Values.OrderBy(a => a.Key))
        {
            float volume = 0;
            try { volume = app.Sessions.Count > 0 ? app.Sessions[0].Control.SimpleAudioVolume.Volume : 0; } catch { }
            apps.Add(new AppSnapshot
            {
                Key = app.Key,
                Name = app.Name,
                Level = app.Level,
                Volume = volume,
                Gain = app.Sessions.Count > 0 ? app.Sessions[0].Applied : 1f,
                State = app.AnyActive ? "播放中" : "静默",
            });
        }

        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var volumes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in settings.Targets)
        {
            states[target.Id] = GetRuntime(target.Id).State;

            var volume = 0f;
            foreach (var process in target.Processes)
            {
                if (!_apps.TryGetValue(Normalize(process), out var app)) continue;
                foreach (var session in app.Sessions)
                {
                    try { volume = MathF.Max(volume, session.Control.SimpleAudioVolume.Volume); }
                    catch { }
                }
            }
            volumes[target.Id] = volume;
        }

        _snapshot = new EngineSnapshot
        {
            MicInUse = _micOwner != null,
            MicOwner = _micOwner,
            MicActive = _micState.Active,
            VideoLevel = videoDb,
            VideoActive = _videoState.Active,
            Enabled = settings.Enabled,
            LastError = _lastError,
            Apps = apps,
            TargetStates = states,
            TargetVolumes = volumes,
        };
    }

    private static float LinearToDb(float linear) =>
        linear <= 0.00001f ? SilenceDb : 20f * MathF.Log10(linear);

    public void Dispose()
    {
        Stop();
        ResetDevices();
    }

    private sealed class SessionRef
    {
        public SessionRef(string instanceId, AudioSessionControl control)
        {
            InstanceId = instanceId;
            Control = control;
        }

        public string InstanceId { get; }
        public AudioSessionControl Control { get; }
        public float Base { get; set; } = -1f;
        public float Applied { get; set; } = -1f;
    }

    private sealed class AppEntry
    {
        public required string Key { get; init; }
        public string Name { get; set; } = "";
        public List<SessionRef> Sessions { get; set; } = [];
        public float Level { get; set; }
        public bool AnyActive { get; set; }
    }
}