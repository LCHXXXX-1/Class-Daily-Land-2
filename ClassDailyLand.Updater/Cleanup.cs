using System.Diagnostics;

namespace ClassDailyLand.Updater;

/// <summary>
/// 收尾清理：把下载的包、临时工作目录和更新器自己删掉。
///
/// 必须借 cmd 延迟执行 —— 更新器不能删除**正在运行的自己**，
/// 所以拉起一个 cmd，先 ping 几秒（那时本进程已退出、句柄已释放）再删。
/// </summary>
internal static class Cleanup
{
    public static void Schedule(string workDir, string zipPath, JobLog log)
    {
        var self = Environment.ProcessPath ?? "";
        var selfDir = self.Length > 0 ? Path.GetDirectoryName(self) : null;

        var script = $"ping 127.0.0.1 -n 3 > nul & del /f /q \"{zipPath}\" > nul 2>&1" +
                     $" & rd /s /q \"{workDir}\" > nul 2>&1";

        if (self.Length > 0)
            script += $" & del /f /q \"{self}\" > nul 2>&1";

        // 更新器是"整坨文件"：exe 删掉后它所在的临时目录也要一并清掉（含依赖 dll）
        if (!string.IsNullOrWhiteSpace(selfDir))
            script += $" & rd /s /q \"{selfDir}\" > nul 2>&1";

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(script);

            Process.Start(info);
            log.Write("已安排清理：下载包、临时工作目录与更新器自身");
        }
        catch (Exception ex)
        {
            // 清理失败不影响「更新已经成功」这个事实；残留文件下次更新会被覆盖
            log.Write("安排清理失败（忽略）：" + ex.Message);
        }
    }
}
