using System.Diagnostics;

namespace ClassDailyLand2.App.Diagnostics;

/// <summary>
/// 关键子进程看门狗：盯着一个子进程，如果它在"允许结束"之前就退出，就报
/// <see cref="Core.Diagnostics.ErrorCodes.ProcessTerminated"/>。
///
/// <para>为什么需要它：主程序自己崩了有 DispatcherUnhandledException 兜着，但**依赖的进程**
/// 被外部结束（任务管理器、杀软、系统内存回收）时，主程序只会看到"功能突然不可用"。
/// 这种情况必须有人盯着，否则用户只会说"插件/更新莫名其妙挂了"。</para>
/// </summary>
public sealed class ProcessWatchdog : IDisposable
{
    readonly Process _process;
    readonly string _displayName;
    readonly Action<int, string> _onUnexpectedExit;
    readonly Action<string>? _log;

    int _expected;

    public ProcessWatchdog(Process process, string displayName, Action<int, string> onUnexpectedExit, Action<string>? log = null)
    {
        _process = process;
        _displayName = displayName;
        _onUnexpectedExit = onUnexpectedExit;
        _log = log;

        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
    }

    /// <summary>标记"这次退出是我们自己要求的"，之后的 Exited 不再当异常。</summary>
    public void ExpectExit() => Interlocked.Exchange(ref _expected, 1);

    void OnExited(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _expected) == 1) return;

        int exitCode;
        try { exitCode = _process.ExitCode; }
        catch { exitCode = -1; }

        var detail = $"{_displayName}（PID {SafePid()}）在任务结束前退出，退出码 {exitCode}。" +
                     "依赖它的功能已不可用，重启程序可恢复。";
        _log?.Invoke(detail);

        try { _onUnexpectedExit(exitCode, detail); }
        catch (Exception ex) { _log?.Invoke("看门狗上报失败：" + ex.Message); }
    }

    string SafePid()
    {
        try { return _process.Id.ToString(); } catch { return "?"; }
    }

    public void Dispose()
    {
        try
        {
            ExpectExit();
            _process.Exited -= OnExited;
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch { /* 退出路径不抛 */ }
        finally
        {
            try { _process.Dispose(); } catch { }
        }
    }
}
