using System.Windows;

namespace ClassDailyLand2.Shared;

/// <summary>
/// 单实例锁：命名 Mutex + 非阻塞探测，口径与主程序 App.xaml.cs 的自带实现一致。
///
///   · <c>Local\</c>：按会话隔离，多用户机器互不影响；提权与非提权在同一会话，
///     锁对两边都可见 —— 正是「更新器/安装器只许跑一个」要的语义。
///   · Mutex 引用静态持有，生命周期 = 进程，不释放不置空；进程异常退出由 OS 回收，
///     下一个实例把 AbandonedMutexException 按「已拿到」处理。
///   · 这里只管「有没有第二个实例」；唤醒/置顶是第一实例自己的事（主程序有命名事件），
///     其余进程撞锁后提示一句就退出。
/// </summary>
public static class SingleInstanceLock
{
    private static Mutex? _held;

    /// <summary>true = 拿到锁，继续跑；false = 已有实例在跑，调用方应当退出。</summary>
    public static bool TryAcquire(string mutexName)
    {
        var mutex = new Mutex(false, mutexName, out _);
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // 前一实例异常结束：此刻锁已经归我，照常继续
        }

        _held = mutex;   // 静态持有到进程退出，绝不能在这里 Dispose
        return true;
    }

    private static readonly List<object> _wakeHandles = new();

    /// <summary>
    /// 第二实例：唤醒已在运行的实例（把它的主窗从最小化/遮挡里捞回前台），自己退出。
    /// 返回 false = 没找到事件（对面其实已经死了）—— 调用方照常退出即可，别弹窗。
    /// </summary>
    public static bool SignalWake(string eventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var wake)) return false;
            using (wake) return wake.Set();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 第一实例：建唤醒事件并监听；回调在池线程上触发，界面动作由调用方自己 Dispatch 回 UI 线程。
    /// 建不起来（同名对象被占等）只是没有唤醒通道，不影响锁本身。
    /// </summary>
    public static void ListenWake(string eventName, Action onWake)
    {
        try
        {
            var wake = new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out _);
            var registration = ThreadPool.RegisterWaitForSingleObject(wake,
                (_, _) => { try { onWake(); } catch { /* 唤醒回调不能把进程带崩 */ } },
                state: null, millisecondsTimeOutInterval: -1, executeOnlyOnce: false);

            // 事件与登记句柄都静态持有到进程退出，防 GC 把等待链拆了
            lock (_wakeHandles)
            {
                _wakeHandles.Add(wake);
                _wakeHandles.Add(registration);
            }
        }
        catch
        {
            // 没有唤醒通道也照常跑
        }
    }

    /// <summary>把窗口从最小化/背后捞回来并抢焦点。</summary>
    public static void Activate(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        // SetForegroundWindow 常被系统前台锁拦下：短暂抬一下 Topmost 是惯例手法。
        // 本来就置顶的窗口（更新器）别去碰它，免得唤醒后反而不置顶了。
        if (!window.Topmost)
        {
            window.Topmost = true;
            window.Topmost = false;
        }

        window.Activate();
        window.Focus();
    }
}
