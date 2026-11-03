using System.Diagnostics;
using System.Text;

namespace CDL2.Updater;

/// <summary>
/// 收尾清理：下载的包、工作目录，以及运行在 %Temp% 里的更新器自身。
///
/// 必须借 cmd 延迟执行 —— 进程不能删除正在运行的自己。
/// 只有当更新器跑在 %Temp% 里（主程序的快速路径先把它整个复制过去）才删自己；
/// 直接从安装目录 Update\ 运行时（手工救援 / 慢路径）绝不能删自己 —— 那会拆掉程序的一部分。
/// </summary>
public static class Cleanup
{
    public static void Schedule(string workDir, string? zipPath, UpdaterLog log)
    {
        var self = Environment.ProcessPath ?? "";
        var selfDir = self.Length > 0 ? Path.GetDirectoryName(self) : null;

        var script = new StringBuilder();
        script.Append("ping 127.0.0.1 -n 4 > nul");

        if (!string.IsNullOrEmpty(zipPath) && File.Exists(zipPath))
            script.Append($" & del /f /q \"{zipPath}\" > nul 2>&1");

        if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
            script.Append($" & rd /s /q \"{workDir}\" > nul 2>&1");

        var inTemp = selfDir is not null &&
                     selfDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

        if (inTemp)
        {
            if (self.Length > 0) script.Append($" & del /f /q \"{self}\" > nul 2>&1");
            script.Append($" & rd /s /q \"{selfDir}\" > nul 2>&1");
        }

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
            info.ArgumentList.Add(script.ToString());

            Process.Start(info);
            log.Write(inTemp ? "已安排清理：临时包、工作目录与 %Temp% 里的更新器副本"
                             : "已安排清理：临时包与工作目录（更新器本体在安装目录里，保留）");
        }
        catch (Exception ex)
        {
            log.Write("安排清理失败（忽略）：" + ex.Message);
        }
    }
}
