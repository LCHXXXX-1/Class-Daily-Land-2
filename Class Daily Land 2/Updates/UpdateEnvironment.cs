using System.Diagnostics;
using System.Security.Principal;

namespace ClassDailyLand.App.Updates;

/// <summary>
/// 更新相关的"环境判定"与"启动更新器"。与业务无关，纯粹是 Windows 上的杂事。
/// </summary>
public static class UpdateEnvironment
{
    /// <summary>程序安装目录（永远是 exe 所在目录，不能用 DataRoot —— 数据目录可能回退到 %LOCALAPPDATA%）。</summary>
    public static string InstallDir
    {
        get
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return dir;
        }
    }

    /// <summary>当前进程是否以管理员身份运行（决定拉起 app 时要不要降权）。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 目录是否可写（写探针）。
    ///
    /// ★ 这是判断"要不要提权"的**唯一依据**：装在 <c>Program Files</c> 时，
    /// 复制第一份文件就会抛 UnauthorizedAccessException，与其让它崩在替换中途，
    /// 不如在启动更新器前就问清楚要不要提权。
    /// </summary>
    public static bool IsDirectoryWritable(string? dir, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(dir))
        {
            error = "目录为空";
            return false;
        }

        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 是不是"从源码直接跑"（开发机）。
    ///
    /// 判定：程序目录里有 <c>*.csproj</c>，或者旁边有 <c>src\</c> / <c>Settings.qml</c> 这类开发痕迹。
    /// 这种情况下自动更新会被 <c>block_update_on_source</c> 拦掉 —— 直接替换开发目录会把源码弄乱。
    /// </summary>
    public static bool IsSourceRun(string installDir)
    {
        try
        {
            if (Directory.EnumerateFiles(installDir, "*.csproj", SearchOption.TopDirectoryOnly).Any()) return true;
            if (Directory.Exists(Path.Combine(installDir, "obj"))) return true;
            if (Directory.Exists(Path.Combine(installDir, "src"))) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>更新器目录（相对安装目录）下的可执行文件名。</summary>
    public const string UpdaterExeName = "ClassDailyLand.Updater.exe";

    /// <summary>
    /// 找到更新器并把它复制到 temp，返回复制后的 exe 路径。
    ///
    /// ★ **必须**复制出来跑：更新器自己也在安装目录里，直接跑的话
    ///   替换阶段会锁住自己（<c>updater\*.dll</c> 复制失败），这是"更新器更新不了自己"的经典问题。
    /// </summary>
    public static string? StageUpdater(string installDir, string updaterDirName, string workDir, out string? error)
    {
        error = null;

        var source = Path.Combine(installDir, updaterDirName ?? "updater");
        if (!Directory.Exists(source))
        {
            error = $"更新器目录不存在：{source}（可能被杀软删了，或这个安装不带更新能力）";
            return null;
        }

        var sourceExe = Path.Combine(source, UpdaterExeName);
        if (!File.Exists(sourceExe))
        {
            error = $"更新器主程序不存在：{sourceExe}";
            return null;
        }

        try
        {
            var target = Path.Combine(workDir, "bin");
            Directory.CreateDirectory(target);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                var dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }

            var staged = Path.Combine(target, UpdaterExeName);
            return File.Exists(staged) ? staged : null;
        }
        catch (Exception ex)
        {
            error = $"复制更新器失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// 启动更新器。
    /// <paramref name="elevated"/> 为 true 时走 UAC 提权（装在 Program Files 时必须）。
    /// 用户在 UAC 对话框里点了"否"会抛 Win32Exception(1223)，调用方要当成"用户放弃"。
    /// </summary>
    public static int? LaunchUpdater(string updaterExe, string jobPath, bool elevated)
    {
        var info = new ProcessStartInfo
        {
            FileName = updaterExe,
            UseShellExecute = true,          // runas 必须
            WorkingDirectory = Path.GetDirectoryName(updaterExe) ?? "",
        };

        // ArgumentList 与 UseShellExecute=true 不能同时用（框架会抛），
        // 这里手动加引号 —— 路径里有中文和空格（"Class Daily Land"）是常态。
        info.Arguments = $"--job \"{jobPath}\"";

        if (elevated) info.Verb = "runas";

        using var process = Process.Start(info);
        return process?.Id;
    }

    /// <summary>把主程序的 <c>app.log</c> 追加一行（更新前/后给用户留痕）。</summary>
    public static void AppendAppLog(string? appLogPath, string line)
    {
        if (string.IsNullOrWhiteSpace(appLogPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(appLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(appLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去不能影响更新流程
        }
    }
}
