using System.Windows;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AutoVolume;

/// <summary>Tray icon, single-instance wake-up and application lifetime.</summary>
internal sealed class AppHost
{
    private readonly Application _app;
    private readonly SettingsStore _store;
    private readonly AudioEngine _engine;
    private readonly WinForms.NotifyIcon _tray;
    private readonly WinForms.ToolStripMenuItem _enabledItem;
    private readonly EventWaitHandle _showEvent;
    private MainWindow? _window;

    public AppHost(Application app)
    {
        _app = app;
        _store = new SettingsStore();
        _engine = new AudioEngine(_store);
        _engine.Start();

        _enabledItem = new WinForms.ToolStripMenuItem("启用闪避", null, (_, _) => ToggleEnabled());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => ShowWindow());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("重置目标音量为 100%", null, (_, _) => _engine.TestCommand = 3);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());
        menu.Opening += (_, _) => _enabledItem.Checked = _store.Settings.Enabled;

        _tray = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateIcon(32),
            Text = "AutoVolume 音量闪避",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ShowWindow();
        };

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AutoVolume.Show");
        var waiter = new Thread(() =>
        {
            while (true)
            {
                _showEvent.WaitOne();
                _app.Dispatcher.Invoke(ShowWindow);
            }
        })
        { IsBackground = true, Name = "ShowWaiter" };
        waiter.Start();

        _app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"未处理异常: {e.Exception}");
            e.Handled = true;
        };

        Log.Write("AutoVolume 启动");
    }

    private void ShowWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow(_store, _engine);
            _window.Closed += (_, _) => _window = null;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void ToggleEnabled()
    {
        _store.Settings.Enabled = !_store.Settings.Enabled;
        _store.SaveSoon();
        Log.Write($"闪避总开关: {(_store.Settings.Enabled ? "开" : "关")}");
    }

    private void Exit()
    {
        Log.Write("AutoVolume 退出");
        _engine.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _window?.CloseForExit();
        _store.Dispose();
        _app.Shutdown();
    }
}