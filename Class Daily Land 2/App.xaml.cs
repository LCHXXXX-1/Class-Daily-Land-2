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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
                // 渲染/定时器异常不应让整个应用消失
                controller.Log?.Error("未处理异常: " + de.Exception);
                de.Handled = true;
            };

            controller.Startup();

            using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName, out _);
            _wakeRegistration = ThreadPool.RegisterWaitForSingleObject(wake,
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
