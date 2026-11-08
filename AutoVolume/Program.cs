using System.Windows;

namespace AutoVolume;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--test-pause", StringComparer.OrdinalIgnoreCase))
        {
            RunPauseTest();
            return;
        }

        if (args.Contains("--reset-volumes", StringComparer.OrdinalIgnoreCase))
        {
            RunResetVolumes();
            return;
        }

        if (args.Contains("--dump", StringComparer.OrdinalIgnoreCase))
        {
            RunDump();
            return;
        }

        _mutex = new Mutex(true, @"Local\AutoVolume.SingleInstance", out var created);
        if (!created)
        {
            try { EventWaitHandle.OpenExisting(@"Local\AutoVolume.Show").Set(); } catch { }
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _ = new AppHost(app);
        app.Run();
    }

    /// <summary>Headless check: pause every target, hold 6 s, resume, then exit.</summary>
    private static void RunPauseTest()
    {
        var store = new SettingsStore();
        var engine = new AudioEngine(store);

        try
        {
            engine.Start();
            Log.Write("=== 自检开始：3 秒后暂停全部目标 ===");
            Thread.Sleep(3000);

            engine.TestCommand = 1;
            Thread.Sleep(6000);

            Log.Write("=== 自检：恢复全部目标 ===");
            engine.TestCommand = 2;
            Thread.Sleep(3000);

            Log.Write("=== 自检结束 ===");
        }
        finally
        {
            engine.Stop();
            store.Dispose();
        }
    }

    private static void RunDump()
    {
        var store = new SettingsStore();
        var engine = new AudioEngine(store);

        try
        {
            engine.Start();
            Thread.Sleep(2500);
            engine.TestCommand = 4;
            Thread.Sleep(500);
        }
        finally
        {
            engine.Stop();
            store.Dispose();
        }
    }

    private static void RunResetVolumes()
    {
        var store = new SettingsStore();
        var engine = new AudioEngine(store);

        try
        {
            engine.Start();
            Thread.Sleep(2500);
            engine.TestCommand = 3;
            Thread.Sleep(1000);
        }
        finally
        {
            engine.Stop();
            store.Dispose();
        }
    }
}