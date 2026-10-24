using System.Diagnostics;
using System.Security.Principal;

namespace ClassDailyLand.Updater;

/// <summary>
/// 进程相关的闸门：等旧进程退出、按路径确认程序真的没了、拉起新程序。
///
/// ★ 「程序还在不在」的判定口径是**完整路径**，不是进程名、更不是单实例 Mutex：
///   - 进程名相同但装在别的目录（开发机上的另一份、便携版）与本次替换无关；
///   - 单实例 Mutex 是**整机全局**的，机器上任何一份实例开着都会误判
///     "仍在运行"（沙箱演练时真实踩到：真程序开着，假安装目录却被拦住）。
/// </summary>
public static class ProcessGate
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>等进程退出。pid &lt;= 0 视为"不用等"。</summary>
    public static bool WaitForExit(int pid, int timeoutMs, Action<string>? log = null)
    {
        if (pid <= 0) return true;

        try
        {
            using var process = Process.GetProcessById(pid);
            log?.Invoke($"等待旧进程 {pid} 退出…");
            if (process.WaitForExit(timeoutMs)) return true;

            log?.Invoke($"旧进程 {pid} 在 {timeoutMs}ms 内没退出，强制结束");
            try { process.Kill(entireProcessTree: true); } catch { }
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;    // 进程不存在 = 已经退出
        }
        catch (Exception ex)
        {
            log?.Invoke("等待进程退出异常：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 按**完整路径**找正在运行的进程（主模块路径与 <paramref name="exePath"/> 相同才算）。
    /// 读不到主模块的进程（权限不足 / 正在退出）不拦，只记录 —— 那时 pid 等待才是主判据。
    /// </summary>
    public static List<Process> FindByPath(string exePath, Action<string>? log = null)
    {
        var found = new List<Process>();
        var unreadable = 0;

        string expected;
        try { expected = Path.GetFullPath(exePath); }
        catch { return found; }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var fileName = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(fileName))
                {
                    process.Dispose();
                    continue;
                }

                if (string.Equals(Path.GetFullPath(fileName), expected, StringComparison.OrdinalIgnoreCase))
                    found.Add(process);
                else
                    process.Dispose();
            }
            catch
            {
                // 取不到路径时不妄下结论（系统进程 / 权限不足），放行；
                // 数量汇总成一行 —— 逐个记会把日志刷成几百行系统进程名。
                unreadable++;
                try { process.Dispose(); } catch { }
            }
        }

        if (unreadable > 0)
            log?.Invoke($"有 {unreadable} 个进程读不到主模块路径（系统进程或权限不足），按放行处理");

        return found;
    }

    /// <summary>等这批进程全部退出；超时还有残留返回 false。</summary>
    public static bool WaitForAll(List<Process> processes, TimeSpan grace, Action<string>? log = null)
    {
        var deadline = DateTime.UtcNow + grace;

        while (processes.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(400);

            for (var i = processes.Count - 1; i >= 0; i--)
            {
                var exited = false;
                try { exited = processes[i].HasExited; }
                catch { exited = true; }   // 取不到状态就当它没了

                if (exited)
                {
                    try { processes[i].Dispose(); } catch { }
                    processes.RemoveAt(i);
                }
            }
        }

        if (processes.Count == 0) return true;

        log?.Invoke($"还有 {processes.Count} 个进程没退出");
        foreach (var p in processes) { try { p.Dispose(); } catch { } }
        return false;
    }

    /// <summary>
    /// 拉起主程序。更新器自己被提权过时用 <c>explorer.exe</c> 降权启动，
    /// 否则 app 会一直以管理员运行。
    /// </summary>
    public static bool Launch(string exePath, string? workDir = null)
    {
        try
        {
            if (!File.Exists(exePath)) return false;

            if (IsElevated)
            {
                // explorer 启动的子进程会继承"普通用户"令牌
                var info = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = false,
                };
                info.ArgumentList.Add(exePath);
                Process.Start(info)?.Dispose();
                return true;
            }

            var start = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = workDir ?? Path.GetDirectoryName(exePath) ?? "",
            };
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            UpdaterLog.Warn("拉起程序失败：" + ex.Message);
            return false;
        }
    }
}
