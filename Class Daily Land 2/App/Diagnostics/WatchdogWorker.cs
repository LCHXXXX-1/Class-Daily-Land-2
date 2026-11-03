namespace ClassDailyLand2.App.Diagnostics;

/// <summary>
/// 看门狗观察进程：自身以 <c>--watchdog-worker</c> 启动的轻量子进程，
/// 除了挂着什么都不做 —— 它存在的意义就是"可以被杀掉"。
///
/// <para>主程序的 <see cref="ProcessWatchdog"/> 盯着它：一旦它提前退出，
/// 就按「关键进程异常退出」走完整的上报链路。这样测试报错制造的是
/// 一次**真实的进程级故障**，而不是一段写死的文字。</para>
/// </summary>
public static class WatchdogWorker
{
    public static void Run()
    {
        // 故意不写任何日志：两个进程抢 app.log 只会互相搅局。
        // 这个进程活着 = 没事；它死了 = 看门狗有活干。
        System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
    }
}
