using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        public required ProgressBar VolumeBar { get; init; }
    }

    private sealed class MeterUi
    {
        public required TextBlock ValueText { get; init; }
        public required ProgressBar Bar { get; init; }
    }

    private static readonly Brush MeterBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF7));
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xEB, 0xCF));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));

    private readonly SettingsStore _store;
    private readonly AudioEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TargetUi> _targets = new(StringComparer.OrdinalIgnoreCase);
    private MeterUi _micMeter = null!;
    private MeterUi _videoMeter = null!;
    private bool _loading = false;

    public MainWindow(SettingsStore store, AudioEngine engine)
    {
        _store = store;
        _engine = engine;
        InitializeComponent();
        Icon = AppIcon.CreateImageSource(64);

        PathText.Text = $"配置：{store.FilePath}";
        HotkeyBox.Text = store.Settings.KuGouPauseHotkey.ToString();
        HotkeyBox.LostFocus += (_, _) => CommitHotkey();
        HotkeyBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHotkey(); };

        RefreshBox.Text = store.Settings.RefreshMs.ToString(CultureInfo.InvariantCulture);
        RefreshBox.LostFocus += (_, _) => CommitRefresh();
        RefreshBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitRefresh(); };

        BuildTriggers();
        BuildTargets();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(store.Settings.RefreshMs) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    // ---- build ----

    private void BuildTriggers()
    {
        _micMeter = BuildTriggerRow("麦克风", _store.Settings.Mic, isMic: true);
        _videoMeter = BuildTriggerRow("视频播放", _store.Settings.Video, isMic: false);
    }

    private MeterUi BuildTriggerRow(string title, TriggerSettings cfg, bool isMic)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = cfg.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        toggle.Checked += (_, _) => { if (!_loading) { cfg.Enabled = true; _store.SaveSoon(); } };
        toggle.Unchecked += (_, _) => { if (!_loading) { cfg.Enabled = false; _store.SaveSoon(); } };
        Grid.SetColumn(toggle, 0);
        grid.Children.Add(toggle);

        var (cell, valueText, bar) = BuildBarLabel(title, 210);
        Grid.SetColumn(cell, 1);
        grid.Children.Add(cell);

        if (isMic)
        {
            AddLabel(grid, "判定方式", 2);
            var occupancy = new TextBlock
            {
                Text = "麦克风占用",
                Style = (Style)FindResource("FieldLabel"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0),
            };
            Grid.SetColumn(occupancy, 3);
            grid.Children.Add(occupancy);
        }
        else
        {
            AddLabel(grid, "阈值 (dB)", 2);
            AddBox(grid, NumberBox(cfg.ThresholdDb, -90, 0, v => { cfg.ThresholdDb = v; _store.SaveSoon(); }), 3);
        }

        AddLabel(grid, "持续 (ms)", 4);
        AddBox(grid, NumberBox(cfg.SustainMs, 0, 5000, v => { cfg.SustainMs = (int)v; _store.SaveSoon(); }), 5);

        AddLabel(grid, "保持 (ms)", 6);
        AddBox(grid, NumberBox(cfg.HoldMs, 0, 5000, v => { cfg.HoldMs = (int)v; _store.SaveSoon(); }), 7);

        TriggerPanel.Children.Add(grid);
        return new MeterUi { ValueText = valueText, Bar = bar };
    }

    private (Grid Cell, TextBlock ValueText, ProgressBar Bar) BuildBarLabel(string text, double width)
    {
        var cell = new Grid { Height = 30, Width = width, VerticalAlignment = VerticalAlignment.Center };

        var bar = new ProgressBar
        {
            Style = (Style)FindResource("WideBar"),
            Minimum = 0,
            Maximum = 1,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var dock = new DockPanel { LastChildFill = true };
        var valueText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            MaxWidth = 120,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(valueText, Dock.Right);

        var caption = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0),
            FontSize = 14,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        };

        dock.Children.Add(valueText);
        dock.Children.Add(caption);

        cell.Children.Add(bar);
        cell.Children.Add(dock);
        return (cell, valueText, bar);
    }

    private void AddLabel(Grid grid, string text, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("FieldLabel"),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private void AddBox(Grid grid, TextBox box, int column)
    {
        box.Margin = new Thickness(0, 0, 18, 0);
        box.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(box, column);
        grid.Children.Add(box);
    }

    private void BuildTargets()
    {
        var grid = new Grid();
        double[] widths = [292, 150, 112, 150, 112, 112];
        foreach (var width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });

        string[] headers = ["目标", "麦克风触发", "压低到", "视频触发", "压低到", "状态"];
        for (var i = 0; i < headers.Length; i++)
        {
            var header = new TextBlock
            {
                Text = headers[i],
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 0, 12, 10),
            };
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
        var nameCell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 12),
        };

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = target.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 44,
        };
        toggle.Checked += (_, _) => { if (!_loading) { target.Enabled = true; _store.SaveSoon(); } };
        toggle.Unchecked += (_, _) => { if (!_loading) { target.Enabled = false; _store.SaveSoon(); } };

        var (barCell, volumeText, volumeBar) = BuildBarLabel(target.Name, 238);
        nameCell.Children.Add(toggle);
        nameCell.Children.Add(barCell);
        Grid.SetColumn(nameCell, 0);
        Grid.SetRow(nameCell, row);
        grid.Children.Add(nameCell);

        var micCombo = ActionCombo(target.OnMic, action =>
        {
            target.OnMic = action;
            _store.SaveSoon();
            UpdatePercentState(target);
        });
        micCombo.Margin = new Thickness(0, 0, 10, 12);
        Grid.SetColumn(micCombo, 1);
        Grid.SetRow(micCombo, row);
        grid.Children.Add(micCombo);

        var micPercent = PercentBox(target.OnMicDuckTo, v => { target.OnMicDuckTo = v / 100.0; _store.SaveSoon(); });
        var micPercentPanel = PercentPanel(micPercent);
        micPercentPanel.Margin = new Thickness(0, 0, 10, 12);
        Grid.SetColumn(micPercentPanel, 2);
        Grid.SetRow(micPercentPanel, row);
        grid.Children.Add(micPercentPanel);

        var videoCombo = ActionCombo(target.OnVideo, action =>
        {
            target.OnVideo = action;
            _store.SaveSoon();
            UpdatePercentState(target);
        });
        videoCombo.Margin = new Thickness(0, 0, 10, 12);
        Grid.SetColumn(videoCombo, 3);
        Grid.SetRow(videoCombo, row);
        grid.Children.Add(videoCombo);

        var videoPercent = PercentBox(target.OnVideoDuckTo, v => { target.OnVideoDuckTo = v / 100.0; _store.SaveSoon(); });
        var videoPercentPanel = PercentPanel(videoPercent);
        videoPercentPanel.Margin = new Thickness(0, 0, 10, 12);
        Grid.SetColumn(videoPercentPanel, 4);
        Grid.SetRow(videoPercentPanel, row);
        grid.Children.Add(videoPercentPanel);

        var state = new TextBlock
        {
            Text = "—",
            Style = (Style)FindResource("Caption"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetColumn(state, 5);
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
            VolumeBar = volumeBar,
        };

        UpdatePercentState(target);
    }

    private ComboBox ActionCombo(TargetAction current, Action<TargetAction> onChanged)
    {
        var combo = new ComboBox
        {
            Style = (Style)FindResource("ActionCombo"),
            ItemsSource = new[] { "不处理", "压低音量", "暂停播放" },
            SelectedIndex = (int)current,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 138,
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
        return NumberBox(fraction * 100, 0, 100, onChanged, width: 74);
    }

    private StackPanel PercentPanel(TextBox box)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        box.Margin = new Thickness(0, 0, 6, 0);
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "%",
            Style = (Style)FindResource("Caption"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private TextBox NumberBox(double value, double min, double max, Action<double> onChanged, double width = 88)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("NumberBox"),
            Text = value.ToString("0", CultureInfo.InvariantCulture),
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                var clamped = Math.Clamp(parsed, min, max);
                box.Text = clamped.ToString("0", CultureInfo.InvariantCulture);
                onChanged(clamped);
            }
            else
            {
                box.Text = value.ToString("0", CultureInfo.InvariantCulture);
            }
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
    }

    // ---- refresh ----

    private void Refresh()
    {
        var snapshot = _engine.Snapshot;

        var enabled = _store.Settings.Enabled;
        RunPillText.Text = enabled ? "● 运行中" : "○ 已停止";
        RunPill.Background = enabled
            ? new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xEA))
            : new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        RunPillText.Foreground = enabled
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextSecondaryBrush");

        ToggleButton.Content = enabled ? "停止闪避" : "开始闪避";
        ToggleButton.Background = enabled
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("AccentBrush");

        _micMeter.ValueText.Text = snapshot.MicInUse ? snapshot.MicOwner ?? "占用中" : "空闲";
        _micMeter.Bar.Value = snapshot.MicInUse ? 1 : 0;
        _micMeter.Bar.Foreground = snapshot.MicActive ? ActiveBrush : MeterBrush;

        _videoMeter.ValueText.Text = FormatDb(snapshot.VideoLevel);
        _videoMeter.Bar.Value = LevelToBar(snapshot.VideoLevel);
        _videoMeter.Bar.Foreground = snapshot.VideoActive ? ActiveBrush : MeterBrush;

        foreach (var (id, ui) in _targets)
        {
            var volume = snapshot.TargetVolumes.TryGetValue(id, out var v) ? v : 0f;
            ui.VolumeBar.Value = Math.Clamp(volume, 0, 1);
            ui.VolumeText.Text = $"{volume * 100:0}%";

            var state = snapshot.TargetStates.TryGetValue(id, out var s) ? s : "—";
            ui.State.Text = state;
            ui.VolumeBar.Foreground = state.Contains("暂停") || state.Contains("禁用")
                ? IdleBrush
                : MeterBrush;
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
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private bool _closing;

    public void CloseForExit()
    {
        _closing = true;
        _timer.Stop();
        Close();
    }
}