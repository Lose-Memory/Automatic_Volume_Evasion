using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoVolume;

public partial class MainWindow : Window
{
    private sealed class TargetUi
    {
        public required ComboBox? MicCombo { get; init; }
        public required ComboBox? VideoCombo { get; init; }
        public required TextBox? MicPercent { get; init; }
        public required TextBox? VideoPercent { get; init; }
        public required TextBlock State { get; init; }
        public required TextBlock VolumeText { get; init; }
        public required SegmentedMeter VolumeMeter { get; init; }
    }

    private sealed class ProgramChoice
    {
        public required string Process { get; init; }
        public required string Display { get; init; }
        public override string ToString() => Display;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kugou"] = "酷狗音乐",
        ["cloudmusic"] = "网易云音乐",
        ["qqmusic"] = "QQ音乐",
        ["spotify"] = "Spotify",
        ["foobar2000"] = "foobar2000",
        ["vlc"] = "VLC",
        ["potplayermini64"] = "PotPlayer",
        ["potplayermini"] = "PotPlayer",
        ["msedge"] = "Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["wmplayer"] = "Windows Media Player",
    };

    /// <summary>Noise that a user will never pick as a music/video program.</summary>
    private static readonly string[] SystemProcesses =
    [
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "svchost", "fontdrvhost", "dwm", "audiodg", "conhost", "ctfmon", "spoolsv",
        "sihost", "taskhostw", "searchhost", "runtimebroker", "shellexperiencehost",
        "textinputhost", "securityhealthservice", "securityhealthsystray", "backgroundtaskhost",
        "applicationframehost", "dllhost", "wmiprvse",
    ];

    private readonly SettingsStore _store;
    private readonly AudioEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TargetUi> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ignored = [];
    private int _musicFilter;
    private int _videoFilter;
    private bool _loading = false;
    private bool? _toggleEnabled;

    public MainWindow(SettingsStore store, AudioEngine engine)
    {
        _store = store;
        _engine = engine;
        InitializeComponent();
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?"}";
        Icon = AppIcon.CreateImageSource(64);
        ApplyTheme();
        RestorePlacement();
        SourceInitialized += (_, _) => ApplyTitleBarTheme();

        PathText.Text = $"配置：{store.FilePath}";

        ScanBox.Text = store.Settings.ScanMsCapture.ToString(CultureInfo.InvariantCulture);
        ScanBox.LostFocus += (_, _) => CommitScan();
        ScanBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitScan(); };

        RefreshBox.Text = store.Settings.RefreshMs.ToString(CultureInfo.InvariantCulture);
        RefreshBox.LostFocus += (_, _) => CommitRefresh();
        RefreshBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitRefresh(); };

        IgnoredBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddIgnored(); };

        BuildTriggers();
        BuildTargetLists();
        _ignored.AddRange(store.Settings.IgnoredCaptureProcesses);
        RenderIgnored();

        ContentRendered += (_, _) =>
        {
            RootScroll.ScrollToTop();
            Dispatcher.BeginInvoke(new Action(() => RootScroll.ScrollToTop()), DispatcherPriority.ApplicationIdle);
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(store.Settings.RefreshMs) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    // ---- build ----

    private void BuildTriggers()
    {
        var mic = _store.Settings.Mic;
        var video = _store.Settings.Video;

        MicSwitch.IsChecked = mic.Enabled;
        MicSwitch.Checked += (_, _) => { if (!_loading) { mic.Enabled = true; _store.SaveSoon(); } };
        MicSwitch.Unchecked += (_, _) => { if (!_loading) { mic.Enabled = false; _store.SaveSoon(); } };
        BindNumber(MicSustainBox, mic.SustainMs, 0, 5000, v => { mic.SustainMs = (int)v; _store.SaveSoon(); });
        BindNumber(MicHoldBox, mic.HoldMs, 0, 5000, v => { mic.HoldMs = (int)v; _store.SaveSoon(); });

        VideoSwitch.IsChecked = video.Enabled;
        VideoSwitch.Checked += (_, _) => { if (!_loading) { video.Enabled = true; _store.SaveSoon(); } };
        VideoSwitch.Unchecked += (_, _) => { if (!_loading) { video.Enabled = false; _store.SaveSoon(); } };
        BindNumber(VideoThresholdBox, video.ThresholdDb, -90, 0, v =>
        {
            video.ThresholdDb = v;
            VideoMeter.Marker = LevelToBar((float)v);
            _store.SaveSoon();
        });
        BindNumber(VideoSustainBox, video.SustainMs, 0, 5000, v => { video.SustainMs = (int)v; _store.SaveSoon(); });
        BindNumber(VideoHoldBox, video.HoldMs, 0, 5000, v => { video.HoldMs = (int)v; _store.SaveSoon(); });

        VideoMeter.Marker = LevelToBar((float)video.ThresholdDb);

        ScanBox.ToolTip = "麦克风占用多久被重新检测一次。越小反应越快，CPU 略增。";
    }

    /// <summary>把 XAML 里已有的输入框接到一个数值上：回车或失焦时提交，非法输入回退。</summary>
    private void BindNumber(TextBox box, double value, double min, double max, Action<double> onChanged)
    {
        var current = value;
        box.Text = current.ToString("0", CultureInfo.InvariantCulture);

        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                current = Math.Clamp(parsed, min, max);
                onChanged(current);
            }
            box.Text = current.ToString("0", CultureInfo.InvariantCulture);
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
    }

    // ---- target lists ----

    private void BuildTargetLists()
    {
        // 默认只看正在出声的程序：那是大多数人添加目标时真正想找的。
        _musicFilter = 2;
        _videoFilter = 2;
        BuildFilter(MusicFilter, TargetCategory.Music);
        BuildFilter(VideoFilter, TargetCategory.Video);
        FillProgramCombo(MusicProgramCombo, TargetCategory.Music);
        FillProgramCombo(VideoProgramCombo, TargetCategory.Video);
        RenderTargets(TargetCategory.Music);
        RenderTargets(TargetCategory.Video);
    }

    private void BuildFilter(StackPanel host, TargetCategory category)
    {
        string[] labels = ["应用进程", "全部进程", "仅正在出声"];
        var selected = category == TargetCategory.Music ? _musicFilter : _videoFilter;

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "RuleBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var radios = new List<RadioButton>();

        for (var i = 0; i < labels.Length; i++)
        {
            var radio = new RadioButton
            {
                Content = labels[i],
                GroupName = $"filter-{category}",
                Style = (Style)FindResource("SegmentRadio"),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                IsChecked = i == selected,
            };
            radios.Add(radio);
            row.Children.Add(radio);
        }

        for (var i = 0; i < radios.Count; i++)
        {
            var index = i;
            radios[i].Checked += (_, _) => SetFilter(category, index);
        }

        border.Child = row;
        host.Children.Add(border);
    }

    private void SetFilter(TargetCategory category, int index)
    {
        if (_loading) return;

        if (category == TargetCategory.Music) _musicFilter = index;
        else _videoFilter = index;

        FillProgramCombo(category == TargetCategory.Music ? MusicProgramCombo : VideoProgramCombo, category);
    }

    private void FillProgramCombo(ComboBox combo, TargetCategory category)
    {
        var filter = category == TargetCategory.Music ? _musicFilter : _videoFilter;

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in _store.Settings.Targets)
            foreach (var process in target.Processes)
                known.Add(process);

        var names = new List<string>();

        if (filter == 2)
        {
            names.AddRange(_engine.Snapshot.Apps
                .Where(a => a.Level > 0.003f)
                .Select(a => a.Name));
        }
        else
        {
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try { names.Add(process.ProcessName); }
                catch { }
                finally { process.Dispose(); }
            }

            if (filter == 0) names = names.Where(n => !IsSystemProcess(n)).ToList();
        }

        var choices = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(CleanProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => known.Contains(n) ? 1 : 0)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new ProgramChoice
            {
                Process = n,
                Display = known.Contains(n) ? $"{Describe(n)} · 已在列表" : Describe(n),
            })
            .ToList();

        if (choices.Count == 0)
        {
            choices.Add(new ProgramChoice
            {
                Process = "",
                Display = filter == 2 ? "当前没有程序在出声" : "没有可选的进程",
            });
        }

        combo.ItemsSource = choices;
        combo.SelectedIndex = 0;
    }

    private void RenderTargets(TargetCategory category)
    {
        var panel = category == TargetCategory.Music ? MusicTargetPanel : VideoTargetPanel;
        panel.Children.Clear();

        var list = _store.Settings.Targets.Where(t => t.Category == category).ToList();
        foreach (var target in list)
            panel.Children.Add(BuildTargetRow(target));

        if (list.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = category == TargetCategory.Music
                    ? "还没有音乐程序。触发时这些程序会被压低或暂停。"
                    : "还没有视频程序。视频播放触发不会生效。",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
            panel.Children.Add(hint);
        }
    }

    private FrameworkElement BuildTargetRow(TargetSettings target)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = target.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        toggle.Checked += (_, _) => { if (!_loading) { target.Enabled = true; _store.SaveSoon(); } };
        toggle.Unchecked += (_, _) => { if (!_loading) { target.Enabled = false; _store.SaveSoon(); } };
        top.Children.Add(toggle);

        var nameCell = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock
        {
            Text = target.Name,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = target.Name,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        nameCell.Children.Add(name);

        var processLine = new TextBlock
        {
            Text = string.Join(" · ", target.Processes),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        processLine.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
        nameCell.Children.Add(processLine);

        Grid.SetColumn(nameCell, 1);
        top.Children.Add(nameCell);

        var volumeMeter = new SegmentedMeter
        {
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        volumeMeter.SetResourceReference(SegmentedMeter.FillBrushProperty, "MeterIdleBrush");
        volumeMeter.SetResourceReference(SegmentedMeter.EmptyBrushProperty, "MeterTrackBrush");
        volumeMeter.SetResourceReference(SegmentedMeter.MarkerBrushProperty, "TextFaintBrush");
        Grid.SetColumn(volumeMeter, 2);
        top.Children.Add(volumeMeter);

        var volumeText = new TextBlock
        {
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        volumeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(volumeText, 3);
        top.Children.Add(volumeText);

        var state = new TextBlock
        {
            Text = "—",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        state.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(state, 4);
        top.Children.Add(state);

        var remove = new Button
        {
            Content = "×",
            Style = (Style)FindResource("ChipButton"),
            ToolTip = "从列表移除",
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) => RemoveTarget(target);
        Grid.SetColumn(remove, 5);
        top.Children.Add(remove);

        outer.Children.Add(top);

        var edit = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(46, 6, 0, 0) };

        edit.Children.Add(Label("麦克风"));
        var micCombo = ActionCombo(target.OnMic, action =>
        {
            target.OnMic = action;
            _store.SaveSoon();
            UpdatePercentState(target);
        }, allowPause: target.Category == TargetCategory.Music);
        edit.Children.Add(micCombo);

        var micPercent = PercentBox(target.OnMicDuckTo, v =>
        {
            target.OnMicDuckTo = v / 100.0;
            _store.SaveSoon();
        });
        edit.Children.Add(micPercent);
        edit.Children.Add(Unit("%"));

        ComboBox? videoCombo = null;
        TextBox? videoPercent = null;
        if (target.Category == TargetCategory.Music)
        {
            edit.Children.Add(Label("视频播放", 20));
            videoCombo = ActionCombo(target.OnVideo, action =>
            {
                target.OnVideo = action;
                _store.SaveSoon();
                UpdatePercentState(target);
            });
            edit.Children.Add(videoCombo);

            videoPercent = PercentBox(target.OnVideoDuckTo, v =>
            {
                target.OnVideoDuckTo = v / 100.0;
                _store.SaveSoon();
            });
            edit.Children.Add(videoPercent);
            edit.Children.Add(Unit("%"));
        }

        edit.Children.Add(Label("渐变", 20));
        var attack = NumberBox(target.AttackMs, 0, 5000, v => { target.AttackMs = (int)v; _store.SaveSoon(); }, width: 56);
        attack.ToolTip = "压低 / 恢复的速度。数值越小变化越快。";
        edit.Children.Add(attack);
        edit.Children.Add(Unit("ms"));

        if (target.Category == TargetCategory.Music)
        {
            edit.Children.Add(Label("暂停热键", 20));
            var hotkey = new TextBox
            {
                Style = (Style)FindResource("NumberBox"),
                Width = 132,
                Text = target.PauseHotkey.ToString(),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "动作选「暂停播放」时，发送给该程序的热键。",
            };

            void CommitHotkey()
            {
                var spec = ParseHotkey(hotkey.Text);
                if (spec == null)
                {
                    hotkey.Text = target.PauseHotkey.ToString();
                    MessageBox.Show(this, "热键格式应为 Ctrl+Shift+Alt+F8 这样的组合。", "格式错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                target.PauseHotkey = spec;
                hotkey.Text = spec.ToString();
                _store.SaveSoon();
            }

            hotkey.LostFocus += (_, _) => CommitHotkey();
            hotkey.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHotkey(); };
            edit.Children.Add(hotkey);
        }

        outer.Children.Add(edit);

        _targets[target.Id] = new TargetUi
        {
            MicCombo = micCombo,
            VideoCombo = videoCombo,
            MicPercent = micPercent,
            VideoPercent = videoPercent,
            State = state,
            VolumeText = volumeText,
            VolumeMeter = volumeMeter,
        };

        UpdatePercentState(target);
        return outer;
    }

    private TextBlock Label(string text, double leftMargin = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(leftMargin, 0, 6, 0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return block;
    }

    private TextBlock Unit(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
        return block;
    }

    private ComboBox ActionCombo(TargetAction current, Action<TargetAction> onChanged, bool allowPause = true)
    {
        var items = allowPause ? new[] { "不处理", "压低音量", "暂停播放" } : new[] { "不处理", "压低音量" };
        var combo = new ComboBox
        {
            Style = (Style)FindResource("ActionCombo"),
            ItemsSource = items,
            SelectedIndex = Math.Clamp((int)current, 0, items.Length - 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            onChanged((TargetAction)combo.SelectedIndex);
        };
        return combo;
    }

    private TextBox PercentBox(double fraction, Action<double> onChanged) =>
        NumberBox(fraction * 100, 0, 100, onChanged, width: 52);

    private TextBox NumberBox(double value, double min, double max, Action<double> onChanged, double width = 58)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("NumberBox"),
            Text = value.ToString("0", CultureInfo.InvariantCulture),
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var current = value;
        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                current = Math.Clamp(parsed, min, max);
                onChanged(current);
            }
            box.Text = current.ToString("0", CultureInfo.InvariantCulture);
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return box;
    }

    private void UpdatePercentState(TargetSettings target)
    {
        if (!_targets.TryGetValue(target.Id, out var ui)) return;
        if (ui.MicPercent != null) ui.MicPercent.IsEnabled = target.OnMic == TargetAction.Duck;
        if (ui.VideoPercent != null) ui.VideoPercent.IsEnabled = target.OnVideo == TargetAction.Duck;
        ui.VolumeMeter.Marker = DuckMarker(target);
    }

    /// <summary>音量条上的刻度：这个目标被压低时会落到哪一格。</summary>
    private static double DuckMarker(TargetSettings target)
    {
        var levels = new List<double>();
        if (target.OnMic == TargetAction.Duck) levels.Add(target.OnMicDuckTo);
        if (target.Category == TargetCategory.Music && target.OnVideo == TargetAction.Duck)
            levels.Add(target.OnVideoDuckTo);
        return levels.Count == 0 ? double.NaN : levels.Min();
    }

    // ---- add / remove programs ----

    private static string FriendlyName(string process) =>
        FriendlyNames.TryGetValue(process, out var name) ? name : process;

    private static bool IsSystemProcess(string name) =>
        SystemProcesses.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        AppSettings.SystemIgnoredProcesses.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string CleanProcessName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private static string Describe(string process)
    {
        var friendly = FriendlyName(process);
        return string.Equals(friendly, process, StringComparison.OrdinalIgnoreCase)
            ? process
            : $"{friendly}（{process}）";
    }

    private void AddMusicFromCombo_Click(object sender, RoutedEventArgs e) => AddFromCombo(TargetCategory.Music);
    private void AddMusicFromText_Click(object sender, RoutedEventArgs e) => AddFromText(TargetCategory.Music);
    private void AddVideoFromCombo_Click(object sender, RoutedEventArgs e) => AddFromCombo(TargetCategory.Video);
    private void AddVideoFromText_Click(object sender, RoutedEventArgs e) => AddFromText(TargetCategory.Video);

    private void AddFromCombo(TargetCategory category)
    {
        var combo = category == TargetCategory.Music ? MusicProgramCombo : VideoProgramCombo;
        if (combo.SelectedItem is not ProgramChoice choice) return;
        AddTargetProcess(category, choice.Process);
    }

    private void AddFromText(TargetCategory category)
    {
        var box = category == TargetCategory.Music ? MusicManualBox : VideoManualBox;
        var text = box.Text;
        box.Clear();
        AddTargetProcess(category, text);
    }

    private void AddTargetProcess(TargetCategory category, string rawName)
    {
        var process = CleanProcessName(rawName);
        if (process.Length == 0) return;

        var label = category == TargetCategory.Music ? "音乐" : "视频";
        var other = category == TargetCategory.Music ? TargetCategory.Video : TargetCategory.Music;

        if (_store.Settings.Targets.Any(t => t.Category == other &&
                t.Processes.Contains(process, StringComparer.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"{process} 已经在另一个列表里。一个程序只能属于音乐或视频其中一类。",
                "无法添加", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_store.Settings.Targets.Any(t => t.Category == category &&
                t.Processes.Contains(process, StringComparer.OrdinalIgnoreCase)))
            return;

        var sameProgram = _store.Settings.Targets.FirstOrDefault(t =>
            t.Category == category &&
            string.Equals(t.Name, FriendlyName(process), StringComparison.OrdinalIgnoreCase));

        if (sameProgram != null)
        {
            sameProgram.Processes = [.. sameProgram.Processes, process];
        }
        else
        {
            _store.Settings.Targets.Add(new TargetSettings
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = FriendlyName(process),
                Category = category,
                Processes = [process],
            });
        }

        _store.SaveSoon();
        Log.Write($"{label}程序: 新增 {process}");
        RenderTargets(category);
        FillProgramCombo(category == TargetCategory.Music ? MusicProgramCombo : VideoProgramCombo, category);
    }

    private void RemoveTarget(TargetSettings target)
    {
        _store.Settings.Targets.Remove(target);
        _targets.Remove(target.Id);
        _store.SaveSoon();
        Log.Write($"移除目标: {target.Name}");
        RenderTargets(target.Category);
        FillProgramCombo(target.Category == TargetCategory.Music ? MusicProgramCombo : VideoProgramCombo,
            target.Category);
    }

    // ---- ignored capture processes ----

    private void AddIgnored_Click(object sender, RoutedEventArgs e) => AddIgnored();

    private void AddIgnored()
    {
        var name = IgnoredBox.Text.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        IgnoredBox.Clear();
        if (name.Length == 0) return;
        if (_ignored.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

        _ignored.Add(name);
        SaveIgnored();
        RenderIgnored();
        Log.Write($"忽略的麦克风占用: 新增 {name}");
    }

    private void SaveIgnored()
    {
        _store.Settings.IgnoredCaptureProcesses = _ignored.ToArray();
        _store.SaveSoon();
    }

    private void ShowSystemIgnored_Changed(object sender, RoutedEventArgs e) => RenderIgnored();

    private void RenderIgnored()
    {
        IgnoredPanel.Children.Clear();

        if (ShowSystemIgnored.IsChecked == true)
        {
            foreach (var name in AppSettings.SystemIgnoredProcesses)
                IgnoredPanel.Children.Add(BuildChip(name, removable: false));
        }

        foreach (var name in _ignored)
            IgnoredPanel.Children.Add(BuildChip(name, removable: true));

        if (_ignored.Count == 0 && ShowSystemIgnored.IsChecked != true)
        {
            var hint = new TextBlock
            {
                Text = "还没有自定义进程。占用麦克风的程序填在这里就不会触发闪避，名字见任务管理器「详细信息」。",
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center,
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
            IgnoredPanel.Children.Add(hint);
        }
    }

    private Border BuildChip(string name, bool removable)
    {
        var chip = new Border
        {
            Padding = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 16, 4),
        };
        if (!removable) chip.ToolTip = "内置系统进程，始终被忽略";

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock
        {
            Text = name,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, removable ? "TextPrimaryBrush" : "TextFaintBrush");
        row.Children.Add(label);

        if (removable)
        {
            var remove = new Button
            {
                Content = "×",
                Style = (Style)FindResource("ChipButton"),
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "从忽略列表移除",
            };
            remove.Click += (_, _) =>
            {
                _ignored.Remove(name);
                SaveIgnored();
                RenderIgnored();
                Log.Write($"忽略的麦克风占用: 移除 {name}");
            };
            row.Children.Add(remove);
        }

        chip.Child = row;
        return chip;
    }

    // ---- refresh ----

    private void Refresh()
    {
        var snapshot = _engine.Snapshot;
        var enabled = _store.Settings.Enabled;

        RunPill.Background = (Brush)FindResource(enabled ? "PillRunningBrush" : "PillStoppedBrush");
        RunPillText.Text = enabled ? "运行中" : "已停止";
        RunPillText.Foreground = (Brush)FindResource(enabled ? "SignalBrush" : "TextSecondaryBrush");
        RunDot.Fill = (Brush)FindResource(enabled ? "SignalBrush" : "TextFaintBrush");

        if (_toggleEnabled != enabled)
        {
            _toggleEnabled = enabled;
            ToggleButton.Style = (Style)FindResource(enabled ? "StopButton" : "MainButton");
            ToggleButton.Content = enabled ? "停止闪避" : "开始闪避";
        }

        MicMeter.Value = snapshot.MicInUse ? 1 : 0;
        MicMeter.FillBrush = (Brush)FindResource(snapshot.MicInUse ? "SignalFillBrush" : "MeterIdleBrush");
        MicReadout.Text = snapshot.MicInUse ? snapshot.MicOwner ?? "占用中" : "空闲";
        MicReadout.ToolTip = snapshot.MicInUse
            ? $"麦克风正被 {snapshot.MicOwner ?? "其他程序"} 占用"
            : "没有程序占用麦克风";
        MicReadout.SetResourceReference(TextBlock.ForegroundProperty,
            snapshot.MicInUse ? "TextPrimaryBrush" : "TextSecondaryBrush");

        VideoMeter.Value = LevelToBar(snapshot.VideoLevel);
        VideoMeter.FillBrush = (Brush)FindResource(snapshot.VideoActive ? "SignalFillBrush" : "MeterIdleBrush");
        VideoReadout.Text = FormatDb(snapshot.VideoLevel);
        VideoReadout.SetResourceReference(TextBlock.ForegroundProperty,
            snapshot.VideoActive ? "TextPrimaryBrush" : "TextSecondaryBrush");

        foreach (var (id, ui) in _targets)
        {
            var volume = snapshot.TargetVolumes.TryGetValue(id, out var v) ? v : 0f;
            ui.VolumeMeter.Value = Math.Clamp(volume, 0, 1);
            ui.VolumeText.Text = $"{volume * 100:0}%";

            var state = snapshot.TargetStates.TryGetValue(id, out var s) ? s : "—";

            if (state == "暂停失败")
            {
                ui.State.Text = "暂停失败";
                ui.State.SetResourceReference(TextBlock.ForegroundProperty, "StopBrush");
                ui.VolumeMeter.SetResourceReference(SegmentedMeter.FillBrushProperty, "MeterIdleBrush");
                continue;
            }

            var disabled = state == "已禁用";
            var active = state.StartsWith("压低", StringComparison.Ordinal) || state == "已暂停";

            ui.State.Text = state;
            ui.State.SetResourceReference(TextBlock.ForegroundProperty,
                disabled ? "TextFaintBrush" : active ? "SignalBrush" : "TextSecondaryBrush");
            ui.VolumeMeter.SetResourceReference(SegmentedMeter.FillBrushProperty,
                disabled ? "MeterTrackBrush" : active ? "SignalFillBrush" : "MeterIdleBrush");
        }
    }

    // ---- actions ----

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _store.Settings.Enabled = !_store.Settings.Enabled;
        _store.SaveSoon();
        Log.Write($"闪避总开关: {(_store.Settings.Enabled ? "开" : "关")}");
        Refresh();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _store.Settings.Theme = _store.Settings.Theme == "Dark" ? "Light" : "Dark";
        _store.SaveSoon();
        ApplyTheme();
        Refresh();
        Log.Write($"界面主题: {_store.Settings.Theme}");
    }

    private void ApplyTheme()
    {
        var source = _store.Settings.Theme == "Dark" ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        if (Resources.MergedDictionaries.Count > 0)
            Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        ThemeButton.Content = _store.Settings.Theme == "Dark" ? "浅色模式" : "深色模式";
        ApplyTitleBarTheme();
    }

    /// <summary>Windows draws the caption itself; DWM's immersive-dark-mode flag is the only way to darken it.</summary>
    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var dark = _store.Settings.Theme == "Dark" ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+), 19 = the 1809-1909 attribute id
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }

    private void RestorePlacement()
    {
        var placement = _store.Settings.Window;

        if (placement.Width >= 600 && placement.Height >= 400)
        {
            Width = placement.Width;
            Height = placement.Height;
        }

        if (placement.HasPosition && IsPlacementVisible(placement.Left, placement.Top, placement.Width, placement.Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (placement.Maximized) WindowState = WindowState.Maximized;

        Log.Write($"恢复窗口: hasPos={placement.HasPosition} pos=({placement.Left},{placement.Top}) " +
                  $"size={placement.Width}x{placement.Height} visible={IsPlacementVisible(placement.Left, placement.Top, placement.Width, placement.Height)} " +
                  $"startup={WindowStartupLocation}");
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds.Width < 200 || bounds.Height < 200) return;

        var placement = _store.Settings.Window;
        placement.Width = bounds.Width;
        placement.Height = bounds.Height;
        placement.Left = bounds.Left;
        placement.Top = bounds.Top;
        placement.HasPosition = true;
        placement.Maximized = WindowState == WindowState.Maximized;
        _store.SaveSoon();
        Log.Write($"保存窗口: pos=({bounds.Left:0},{bounds.Top:0}) size={bounds.Width:0}x{bounds.Height:0} state={WindowState}");
    }

    private static bool IsPlacementVisible(double left, double top, double width, double height)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;

        return left + width > vl && left < vl + vw && top + height > vt && top < vt + vh;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppContext.BaseDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void CommitScan()
    {
        if (!int.TryParse(ScanBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            ScanBox.Text = _store.Settings.ScanMsCapture.ToString(CultureInfo.InvariantCulture);
            return;
        }

        ms = Math.Clamp(ms, 50, 5000);
        ScanBox.Text = ms.ToString(CultureInfo.InvariantCulture);
        _store.Settings.ScanMsCapture = ms;
        _store.SaveSoon();
    }

    private void CommitRefresh()
    {
        if (!int.TryParse(RefreshBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            RefreshBox.Text = _store.Settings.RefreshMs.ToString(CultureInfo.InvariantCulture);
            return;
        }

        ms = Math.Clamp(ms, 100, 5000);
        RefreshBox.Text = ms.ToString(CultureInfo.InvariantCulture);
        _store.Settings.RefreshMs = ms;
        _store.SaveSoon();

        if (_timer != null)
            _timer.Interval = TimeSpan.FromMilliseconds(ms);
    }

    private static HotkeySpec? ParseHotkey(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var spec = new HotkeySpec { Key = parts[^1] };
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": spec.Ctrl = true; break;
                case "shift": spec.Shift = true; break;
                case "alt": spec.Alt = true; break;
                default: return null;
            }
        }

        return spec.Ctrl || spec.Shift || spec.Alt ? spec : null;
    }

    private static string FormatDb(float db) => db <= -99f ? "-∞" : $"{db:0} dB";

    private static double LevelToBar(float db)
    {
        if (db <= -60f) return 0;
        return Math.Clamp((db + 60f) / 60f, 0, 1);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing)
        {
            SavePlacement();
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private bool _closing;

    public void CloseForExit()
    {
        SavePlacement();
        _closing = true;
        _timer.Stop();
        Close();
    }
}