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
        public required ComboBox MicCombo { get; init; }
        public required ComboBox VideoCombo { get; init; }
        public required TextBox MicPercent { get; init; }
        public required TextBox VideoPercent { get; init; }
        public required TextBlock State { get; init; }
        public required TextBlock VolumeText { get; init; }
        public required SegmentedMeter VolumeMeter { get; init; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly SettingsStore _store;
    private readonly AudioEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TargetUi> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ignored = [];
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
        HotkeyBox.Text = store.Settings.KuGouPauseHotkey.ToString();
        HotkeyBox.LostFocus += (_, _) => CommitHotkey();
        HotkeyBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHotkey(); };

        RefreshBox.Text = store.Settings.RefreshMs.ToString(CultureInfo.InvariantCulture);
        RefreshBox.LostFocus += (_, _) => CommitRefresh();
        RefreshBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitRefresh(); };

        IgnoredBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddIgnored(); };

        BuildTriggers();
        BuildTargets();
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

    private const double LaneColumnWidth = 196;
    private const double StateColumnWidth = 104;

    private void BuildTargets()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LaneColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LaneColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StateColumnWidth) });

        string[] headers = ["目标", "麦克风触发", "视频触发", "状态"];
        for (var i = 0; i < headers.Length; i++)
        {
            var header = new TextBlock
            {
                Text = headers[i],
                FontSize = 12,
                Margin = new Thickness(i == 0 ? 0 : 16, 0, 0, 9),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(header, i);
            Grid.SetRow(header, 0);
            grid.Children.Add(header);
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var row = 1;
        foreach (var target in _store.Settings.Targets)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddTargetRow(grid, row++, target);
        }

        TargetPanel.Children.Add(grid);
    }

    private void AddTargetRow(Grid grid, int row, TargetSettings target)
    {
        AddRowRule(grid, row);

        var nameCell = new Grid { Margin = new Thickness(0, 8, 16, 8) };
        nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = target.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        toggle.Checked += (_, _) => { if (!_loading) { target.Enabled = true; _store.SaveSoon(); } };
        toggle.Unchecked += (_, _) => { if (!_loading) { target.Enabled = false; _store.SaveSoon(); } };
        nameCell.Children.Add(toggle);

        var name = new TextBlock
        {
            Text = target.Name,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = target.Name,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetColumn(name, 1);
        nameCell.Children.Add(name);

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
        nameCell.Children.Add(volumeMeter);

        var volumeText = new TextBlock
        {
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        volumeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(volumeText, 3);
        nameCell.Children.Add(volumeText);

        Grid.SetColumn(nameCell, 0);
        Grid.SetRow(nameCell, row);
        grid.Children.Add(nameCell);

        var micCombo = ActionCombo(target.OnMic, action =>
        {
            target.OnMic = action;
            _store.SaveSoon();
            UpdatePercentState(target);
        });
        var micPercent = PercentBox(target.OnMicDuckTo, v =>
        {
            target.OnMicDuckTo = v / 100.0;
            _store.SaveSoon();
            UpdatePercentState(target);
        });
        var micCell = LaneCell(micCombo, micPercent);
        Grid.SetColumn(micCell, 1);
        Grid.SetRow(micCell, row);
        grid.Children.Add(micCell);

        var videoCombo = ActionCombo(target.OnVideo, action =>
        {
            target.OnVideo = action;
            _store.SaveSoon();
            UpdatePercentState(target);
        });
        var videoPercent = PercentBox(target.OnVideoDuckTo, v =>
        {
            target.OnVideoDuckTo = v / 100.0;
            _store.SaveSoon();
            UpdatePercentState(target);
        });
        var videoCell = LaneCell(videoCombo, videoPercent);
        Grid.SetColumn(videoCell, 2);
        Grid.SetRow(videoCell, row);
        grid.Children.Add(videoCell);

        var state = new TextBlock
        {
            Text = "—",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        state.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(state, 3);
        Grid.SetRow(state, row);
        grid.Children.Add(state);

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
    }

    private static void AddRowRule(Grid grid, int row)
    {
        var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
        rule.SetResourceReference(Border.BackgroundProperty, "RuleBrush");
        Grid.SetRow(rule, row);
        Grid.SetColumnSpan(rule, 4);
        grid.Children.Add(rule);
    }

    private StackPanel LaneCell(ComboBox combo, TextBox percent)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 4, 0),
        };
        combo.Margin = new Thickness(0, 0, 6, 0);
        panel.Children.Add(combo);
        panel.Children.Add(percent);
        panel.Children.Add(new TextBlock
        {
            Text = "%",
            Style = (Style)FindResource("ParamUnit"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        });
        return panel;
    }

    private ComboBox ActionCombo(TargetAction current, Action<TargetAction> onChanged)
    {
        var combo = new ComboBox
        {
            Style = (Style)FindResource("ActionCombo"),
            ItemsSource = new[] { "不处理", "压低音量", "暂停播放" },
            SelectedIndex = (int)current,
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            onChanged((TargetAction)combo.SelectedIndex);
        };
        return combo;
    }

    private TextBox PercentBox(double fraction, Action<double> onChanged)
    {
        return NumberBox(fraction * 100, 0, 100, onChanged, width: 52);
    }

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
        ui.MicPercent.IsEnabled = target.OnMic == TargetAction.Duck;
        ui.VideoPercent.IsEnabled = target.OnVideo == TargetAction.Duck;
        ui.VolumeMeter.Marker = DuckMarker(target);
    }

    /// <summary>音量条上的刻度：这个目标被压低时会落到哪一格。</summary>
    private static double DuckMarker(TargetSettings target)
    {
        var levels = new List<double>();
        if (target.OnMic == TargetAction.Duck) levels.Add(target.OnMicDuckTo);
        if (target.OnVideo == TargetAction.Duck) levels.Add(target.OnVideoDuckTo);
        return levels.Count == 0 ? double.NaN : levels.Min();
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

            if (state.StartsWith("暂停中", StringComparison.Ordinal))
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

    private void CommitHotkey()
    {
        var spec = ParseHotkey(HotkeyBox.Text);
        if (spec == null)
        {
            HotkeyBox.Text = _store.Settings.KuGouPauseHotkey.ToString();
            MessageBox.Show(this, "热键格式应为 Ctrl+Shift+Alt+F8 这样的组合。", "格式错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _store.Settings.KuGouPauseHotkey = spec;
        HotkeyBox.Text = spec.ToString();
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