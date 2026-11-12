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

        if (args.Contains("--bench-scan", StringComparer.OrdinalIgnoreCase))
        {
            RunScanBench(args);
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

    /// <summary>
/// Dev-only benchmark: runs the engine for N seconds without touching any volume and logs
/// per-scan timings. Usage: --bench-scan [--bench-seconds 60] [--bench-interval 200]
/// </summary>
private static void RunScanBench(string[] args)
{
    var seconds = 60;
    var interval = 1000;
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--bench-seconds", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[i + 1], out var s)) seconds = Math.Clamp(s, 5, 600);
        if (args[i].Equals("--bench-interval", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[i + 1], out var ms)) interval = Math.Clamp(ms, 50, 10000);
    }

    var store = new SettingsStore();
    store.Settings.ScanMsCapture = interval;
    var engine = new AudioEngine(store) { DryRun = true };
    var proc = System.Diagnostics.Process.GetCurrentProcess();

    try
    {
        var cpu0 = proc.TotalProcessorTime;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        engine.Start();
        Thread.Sleep(seconds * 1000);
        wall.Stop();
        proc.Refresh();
        var cpu = proc.TotalProcessorTime - cpu0;

        Log.Write($"=== 扫描基准 interval={interval}ms duration={seconds}s ===");
        Log.Write($"进程 CPU: {cpu.TotalMilliseconds:0.0} ms / {wall.Elapsed.TotalSeconds:0.0} s " +
                  $"= {cpu.TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100:0.00}% 单核");
        Log.Write(engine.ScanStats());
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