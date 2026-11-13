using System.Windows;
using System.Windows.Input;

namespace ClassDailyLand2.App;

/// <summary>
/// 应用入口：单实例锁（Mutex）+ 第二实例唤醒 + 全局异常留痕。
/// 窗口全部为代码构建，XAML 仅承载 Application。
/// </summary>
public partial class App : Application
{
    const string MutexName = @"Local\ClassDailyLand2.SingleInstance.v1";
    const string WakeEventName = @"Local\ClassDailyLand2.Wake.v1";

    public AppController? Controller { get; private set; }

    Mutex? _mutex;
    RegisteredWaitHandle? _wakeRegistration;

    /// <summary>唤醒用的命名事件句柄。必须由字段持有 —— 见 OnStartup 里的说明。</summary>
    EventWaitHandle? _wakeEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 特殊入口：不启动主程序、不碰单实例锁 ──
        // 必须放在最前面：报信进程不该因为"已经有一个主程序在跑"而被单实例锁挡出去。
        var args = e.Args ?? Array.Empty<string>();
        if (args.Length > 0 && string.Equals(args[0], "--error-report", StringComparison.OrdinalIgnoreCase))
        {
            // 报信进程：只显示一个报错窗口，关掉就退出。
            // 主程序报错时会先写好报告 JSON 再拉起它 —— 主程序随后挂了也不影响。
            Views.Dialogs.ErrorReportWindow.RunReporter(args.Length > 1 ? args[1] : "");
            Shutdown();
            return;
        }
        if (args.Length > 0 && string.Equals(args[0], "--watchdog-worker", StringComparison.OrdinalIgnoreCase))
        {
            // 看门狗观察进程：安静地挂着，被杀掉才有价值
            Diagnostics.WatchdogWorker.Run();
            Shutdown();
            return;
        }
        if (args.Length > 0 && string.Equals(args[0], "--reinstall-latest", StringComparison.OrdinalIgnoreCase))
        {
            // 无头重装：报信窗的「重新安装」在主程序已经不可信时走这里。
            // 独立进程里重建更新上下文 → 拉起更新器 → 退出；不显示任何窗口。
            RunHeadlessReinstall(args.Length > 1 ? args[1] : "");
            Shutdown();
            return;
        }

        // 触屏/触笔：关掉"按住不放弹圆圈"的系统反馈（各个 Window 在 Loaded 时统一设一遍）。
        // 圈圈是系统在自己的浮层上画的，会盖在自绘的岛/主窗/下拉面板上，看着像卡住了。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) Stylus.SetIsPressAndHoldEnabled(window, false);
            }));

        // 启动早期（Logger 尚未就绪）的异常也要留痕，便于排查
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => WriteCrashLog(ex.ExceptionObject);

        // 非 initialOwnership：构造不阻塞；用 WaitOne(0) 立即探测
        var mutex = new Mutex(false, MutexName, out _);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;   // 前一实例异常结束，视为拿到锁
        }

        // 重启场景：旧实例还在收尾（几秒内就会退出），等它释放锁，而不是把它唤醒
        var restartRequested = args.Any(a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase));
        if (!acquired && restartRequested)
        {
            for (var i = 0; i < 40 && !acquired; i++)
            {
                Thread.Sleep(250);
                try { acquired = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { acquired = true; }
            }
        }

        if (!acquired)
        {
            // 第二实例：唤醒已运行实例，自己退出
            WakeExistingInstance();
            Shutdown();
            return;
        }
        _mutex = mutex;   // 生命周期 = 应用生命周期（OnExit 释放）

        var controller = new AppController(this);
        Controller = controller;

        try
        {
            DispatcherUnhandledException += (_, de) =>
            {
                // 渲染/定时器异常不应让整个应用消失。
                // 但也不能只写日志就完事：用户看到的会是"点了没反应"，所以统一弹一次报错窗
                // （窗口里能导出日志 —— 那比让他截个图有用得多）。
                de.Handled = true;
                Views.Dialogs.ErrorReportWindow.Report("未处理异常", de.Exception.Message, de.Exception);
            };

            controller.Startup();

            // 调试入口：--show-settings 直接把设置中心打开（截图/回归验证用）
            if (args.Any(a => string.Equals(a, "--show-settings", StringComparison.OrdinalIgnoreCase)))
                Dispatcher.BeginInvoke(() => controller.EnsureSettings());
            // 调试入口：--show-page=island 打开设置中心并直接跳到指定页（回归验证用）
            var pageArg = args.FirstOrDefault(a => a.StartsWith("--show-page=", StringComparison.OrdinalIgnoreCase));
            if (pageArg is not null)
                Dispatcher.BeginInvoke(() => controller.ShowSettings(pageArg["--show-page=".Length..]));


            // ★ 句柄必须活到进程结束：命名事件在**最后一个句柄关闭的瞬间**名字就没了，
            //   第二实例的 OpenExisting 会失败 → 双击图标什么也不会发生。
            //   原来写成 `using var`（作用域到 try 块末尾），等于注册完立刻销毁。
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName, out _);
            _wakeRegistration = ThreadPool.RegisterWaitForSingleObject(_wakeEvent,
                (_, _) => Dispatcher.BeginInvoke(() => controller.ShowFromSecondInstance()),
                state: null, millisecondsTimeOutInterval: -1, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeRegistration?.Unregister(null);
        _wakeEvent?.Dispose();      // 进程要走了，这时才可以放掉命名事件
        Controller?.Shutdown();
        base.OnExit(e);
    }

    void WakeExistingInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(WakeEventName);
            ev.Set();
        }
        catch { /* 原实例正在退出 */ }
    }

    /// <summary>
    /// 无头重装：报信窗的「重新安装」走这里 —— 主程序已经不可信时，不能指望它自己更新自己。
    /// 在独立进程里重建更新上下文（按数据目录找设置与更新源），拉起更新器后退出。
    /// </summary>
    static void RunHeadlessReinstall(string dataRoot)
    {
        // 用 Task.Run 隔离同步等待：OnStartup 所在线程带着 Dispatcher 同步上下文，
        // 直接 GetResult() 会和异步续体互相等待（死锁）。
        var result = Task.Run(() =>
        {
            var paths = string.IsNullOrWhiteSpace(dataRoot)
                ? ClassDailyLand2.Core.Storage.DataPaths.Create()
                : ClassDailyLand2.Core.Storage.DataPaths.CreateAt(dataRoot);

            var log = new Core.Logger(paths);
            Core.Logger.Current = log;
            log.Info("===== 无头重装开始（--reinstall-latest）=====");

            try
            {
                var settings = new Core.Managers.SettingsManager(paths);
                settings.Load();

                var service = new Updates.UpdateService(settings, paths, log.Info);
                var r = service.ReinstallLatestAsync().GetAwaiter().GetResult();

                log.Info(r.Ok ? "[重装] 更新器已启动，本进程退出" : "[重装] 失败：" + r.Message);
                return r;
            }
            catch (Exception ex)
            {
                log.Error("[重装] 异常：" + ex);
                return Updates.UpdateLaunchResult.Fail(ex.Message);
            }
        }).GetAwaiter().GetResult();

        if (!result.Ok)
        {
            // 无窗口可弹（无头模式），失败写进留痕文件，下次排查有迹可循
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClassDailyLand2");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "startup-crash.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [重装] 失败：{result.Message}{Environment.NewLine}");
            }
            catch { /* 尽力而为 */ }
        }
    }

    static void WriteCrashLog(object? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassDailyLand2");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup-crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch { /* 尽力而为 */ }
    }
}
