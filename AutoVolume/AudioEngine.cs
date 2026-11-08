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
    public float MicLevel { get; init; }
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
    private WasapiCapture? _micCapture;
    private float _micPeak;

    private DateTime _lastSessionScan = DateTime.MinValue;
    private DateTime _lastDeviceScan = DateTime.MinValue;
    private DateTime _micCaptureRetryAt = DateTime.MinValue;
    private bool _reportedNoMic;
    private bool _deviceScanLogged;

    private readonly TriggerState _micState = new();
    private readonly TriggerState _videoState = new();

    private List<MediaSessionInfo> _mediaSessions = [];
    private DateTime _mediaSessionsAt = DateTime.MinValue;

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
        if (now - _lastSessionScan >= TimeSpan.FromSeconds(1))
        {
            ScanSessions();
            _lastSessionScan = now;
        }

        ReadLevels();
        UpdateMicCapture(settings, now);

        var micDb = LinearToDb(_micPeak);
        var videoDb = LinearToDb(VideoLevel(settings));

        var micWasActive = _micState.Active;
        var videoWasActive = _videoState.Active;
        Evaluate(_micState, settings.Mic, micDb, now);
        Evaluate(_videoState, settings.Video, videoDb, now);
        if (micWasActive != _micState.Active) Log.Write($"麦克风触发: {(_micState.Active ? "开" : "关")} ({micDb:0} dB)");
        if (videoWasActive != _videoState.Active) Log.Write($"视频触发: {(_videoState.Active ? "开" : "关")} ({videoDb:0} dB)");

        RefreshMediaSessions(settings, now);

        var command = Interlocked.Exchange(ref _testCommand, 0);
        if (command == 1)
        {
            _testPauseUntil = now.AddSeconds(5);
            _mediaSessions = MediaControl.GetSessions();
            _mediaSessionsAt = now;
            Log.Write("测试：暂停全部目标");

            foreach (var target in settings.Targets)
            {
                if (!target.Enabled) continue;
                var runtime = GetRuntime(target.Id);
                var playing = IsPlaying(target);
                Log.Write($"  {target.Name}: IsPlaying={playing}");

                var paused = target.PauseMethod == PauseMethod.Hotkey
                    ? playing && Pause(target)
                    : Pause(target);

                if (paused) runtime.PausedByUs = true;
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
            Publish(settings, micDb, videoDb);
            return;
        }

        foreach (var target in settings.Targets)
        {
            var runtime = GetRuntime(target.Id);
            ApplyTarget(target, runtime, settings, micDb, videoDb, elapsedMs);
        }

        Publish(settings, micDb, videoDb);
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

            if (!_deviceScanLogged)
            {
                _deviceScanLogged = true;
                Log.Write($"设备扫描: 输出 {devices.Count} 个, 默认录音 = {_defaultCapture?.FriendlyName ?? "无"}, " +
                          $"麦克风触发 = {_store.Settings.Mic.Enabled}");
            }

            StopMicCapture();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log.Write($"扫描音频设备失败: {ex.Message}");
        }
    }

    private void ResetDevices()
    {
        StopMicCapture();
        foreach (var device in _renderDevices)
        {
            try { device.Dispose(); } catch { }
        }
        _renderDevices = [];
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

    private float VideoLevel(AppSettings settings)
    {
        var level = 0f;
        foreach (var key in settings.VideoSourceProcesses)
        {
            if (_apps.TryGetValue(Normalize(key), out var app))
                level = MathF.Max(level, app.Level);
        }
        return level;
    }

    // ---- mic ----

    private void UpdateMicCapture(AppSettings settings, DateTime now)
    {
        // Meter always runs so the UI shows a live level whether or not ducking is enabled.
        if (_defaultCapture == null)
        {
            if (!_reportedNoMic)
            {
                _reportedNoMic = true;
                Log.Write("未找到默认录音设备，麦克风电平不可用");
            }
            _micPeak = 0f;
            StopMicCapture();
            return;
        }

        if (_micCapture != null) return;
        if (now < _micCaptureRetryAt) return;

        try
        {
            _micCapture = new WasapiCapture(_defaultCapture);
            _micCapture.DataAvailable += OnMicData;
            _micCapture.RecordingStopped += (_, _) => { };
            _micCapture.StartRecording();
            Log.Write($"麦克风电平采集已启动: {_micCapture.WaveFormat}");
        }
        catch (Exception ex)
        {
            _micCaptureRetryAt = now.AddSeconds(5);
            StopMicCapture();
            _lastError = ex.Message;
            Log.Write($"启动麦克风采集失败: {ex.Message}");
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var format = _micCapture?.WaveFormat;
            if (format == null) return;

            var peak = 0f;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                for (var i = 0; i + 3 < e.BytesRecorded; i += 4)
                    peak = MathF.Max(peak, MathF.Abs(BitConverter.ToSingle(e.Buffer, i)));
            }
            else if (format.BitsPerSample == 16)
            {
                for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                    peak = MathF.Max(peak, MathF.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
            }

            _micPeak = MathF.Max(peak, _micPeak * 0.5f);
        }
        catch { }
    }

    private void StopMicCapture()
    {
        if (_micCapture == null) return;
        try { _micCapture.StopRecording(); } catch { }
        try { _micCapture.Dispose(); } catch { }
        _micCapture = null;
        _micPeak = 0f;
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
        float micDb, float videoDb, float elapsedMs)
    {
        if (!settings.Enabled || !target.Enabled)
        {
            if (runtime.PausedByUs) Resume(target, runtime);
            runtime.Gain = 1f;
            runtime.State = "已禁用";
            ApplyVolume(target, 1f);
            return;
        }

        var isVideoSource = target.Processes.Any(p =>
            settings.VideoSourceProcesses.Contains(p, StringComparer.OrdinalIgnoreCase));

        var wantPause = false;
        var duckTo = 1f;
        var ducking = false;

        if (_micState.Active && target.OnMic != TargetAction.None)
        {
            if (target.OnMic == TargetAction.Pause) wantPause = true;
            else { ducking = true; duckTo = MathF.Min(duckTo, (float)target.OnMicDuckTo); }
        }

        if (_videoState.Active && !isVideoSource && target.OnVideo != TargetAction.None)
        {
            if (target.OnVideo == TargetAction.Pause) wantPause = true;
            else { ducking = true; duckTo = MathF.Min(duckTo, (float)target.OnVideoDuckTo); }
        }

        if (wantPause)
        {
            runtime.Gain = 1f;
            if (!runtime.PausedByUs && IsPlaying(target))
            {
                if (Pause(target)) runtime.PausedByUs = true;
            }
            runtime.State = runtime.PausedByUs ? "已暂停" : "暂停中（未能暂停）";
            var pauseCoarse = runtime.PausedByUs ? "已暂停" : "暂停失败";
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
                            session.Base = volume;
                            SaveVolume(key, volume);
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

    private void RefreshMediaSessions(AppSettings settings, DateTime now)
    {
        var needed = settings.Targets.Any(t => t.Enabled && t.PauseMethod == PauseMethod.MediaSession &&
            (t.OnMic == TargetAction.Pause || t.OnVideo == TargetAction.Pause));
        if (!needed)
        {
            _mediaSessions = [];
            return;
        }

        if (now - _mediaSessionsAt < TimeSpan.FromMilliseconds(500)) return;
        _mediaSessionsAt = now;
        _mediaSessions = MediaControl.GetSessions();
    }

    private MediaSessionInfo? FindMediaSession(TargetSettings target)
    {
        foreach (var session in _mediaSessions)
        {
            if (target.MediaSessionPrefixes.Length == 0) continue;
            foreach (var prefix in target.MediaSessionPrefixes)
            {
                if (!string.IsNullOrEmpty(prefix) &&
                    session.Aumid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return session;
            }
        }
        return null;
    }

    private bool IsPlaying(TargetSettings target)
    {
        if (target.PauseMethod == PauseMethod.Hotkey)
        {
            // Session state stays "active" for a while after a player pauses, so trust the meter.
            foreach (var process in target.Processes)
            {
                if (_apps.TryGetValue(Normalize(process), out var app) && app.Level > 0.003f)
                    return true;
            }
            return false;
        }

        return FindMediaSession(target)?.Playing == true;
    }

    private bool Pause(TargetSettings target)
    {
        if (target.PauseMethod == PauseMethod.Hotkey)
        {
            Log.Write($"{target.Name}: 发送暂停热键 {_store.Settings.KuGouPauseHotkey}");
            return MediaControl.SendHotkey(_store.Settings.KuGouPauseHotkey);
        }

        _mediaSessions = MediaControl.GetSessions();
        _mediaSessionsAt = DateTime.UtcNow;

        var session = FindMediaSession(target);
        if (session == null)
        {
            Log.Write($"{target.Name}: 未找到媒体会话");
            return false;
        }

        var ok = MediaControl.Pause(session);
        Log.Write($"{target.Name}: 暂停媒体会话 {session.Aumid} -> {ok}");
        return ok;
    }

    private void Resume(TargetSettings target, TargetRuntime runtime)
    {
        runtime.PausedByUs = false;

        if (target.PauseMethod == PauseMethod.Hotkey)
        {
            // The hotkey is a toggle: only send it while the app is actually paused.
            if (IsPlaying(target))
            {
                Log.Write($"{target.Name}: 已在播放，跳过恢复");
                return;
            }

            Log.Write($"{target.Name}: 发送恢复热键");
            MediaControl.SendHotkey(_store.Settings.KuGouPauseHotkey);
            return;
        }

        _mediaSessions = MediaControl.GetSessions();
        _mediaSessionsAt = DateTime.UtcNow;

        var session = FindMediaSession(target);
        if (session == null) return;
        if (session.Playing)
        {
            Log.Write($"{target.Name}: 已在播放，跳过恢复");
            return;
        }

        var ok = MediaControl.Play(session);
        Log.Write($"{target.Name}: 恢复媒体会话 -> {ok}");
    }

    // ---- snapshot ----

    private void Publish(AppSettings settings, float micDb, float videoDb)
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
            MicLevel = micDb,
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