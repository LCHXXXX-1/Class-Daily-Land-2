using ClassDailyLand2.UpdateContract;
using System.Diagnostics;
using System.Security.Principal;

namespace ClassDailyLand2.App.Updates;

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
    /// 更新器文件名。更新器是"框架依赖 + 单文件夹"，整个文件夹放在安装目录的 <c>Update\</c> 下
    /// （构建时由主工程 csproj 的 <c>CopyUpdater</c> 拷过去）。
    /// </summary>
    public const string UpdaterExeName = "Updater.exe";

    /// <summary>新版更新器所在子目录（整个文件夹一起发布）。</summary>
    public const string NewUpdaterDir = "Update";

    /// <summary>
    /// 更新器的**固定落点**：<c>&lt;程序目录&gt;\Update\Updater.exe</c>。
    /// 构建时由主工程 csproj 的 CopyUpdater 整目录拷到这儿。
    /// </summary>
    public static string PrimaryUpdaterPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, NewUpdaterDir, UpdaterExeName);

    /// <summary>
    /// 找到更新器并把它复制到 temp，返回复制后的 exe 路径。
    ///
    /// ★ **必须**复制出来跑：更新器自己也在安装目录里，直接跑的话
    ///   替换阶段会锁住自己（Windows 不允许删除/覆盖正在运行的 exe），
    ///   这是"更新器更新不了自己"的经典问题。
    ///
    /// ★ 框架依赖的更新器是"exe + 一堆 dll + deps.json + runtimeconfig.json"，
    ///   所以**整目录**搬；只搬 exe 会得到一个"找不到依赖"的坏更新器。
    /// </summary>
    public static string? StageUpdater(string installDir, string workDir, out string? error)
    {
        error = null;

        var sourceExe = PrimaryUpdaterPath;
        if (!File.Exists(sourceExe))
        {
            error = $"没有找到更新器：{sourceExe}（可能被杀软删了，或这个安装不带更新能力）";
            return null;
        }

        var sourceDir = Path.GetDirectoryName(sourceExe)!;
        var exeName = Path.GetFileName(sourceExe);

        try
        {
            var target = Path.Combine(workDir, "bin");
            Directory.CreateDirectory(target);

            // 整个 Update\ 文件夹一起搬（含所有依赖 dll）
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var destination = Path.Combine(target, relative);

                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                File.Copy(file, destination, overwrite: true);
            }

            var staged = Path.Combine(target, exeName);
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
    ///
    /// ★ 参数同时给两份：<c>--job</c>（权威来源，带 pid / 保留名单 / 结果落点）与
    ///   <c>--url / --hash / --target / --exe</c>（更新器也支持的手工/救援入口）。
    ///   更新器优先读 job.json；多给的四个参数只是让命令行本身可读、可复现。
    /// </summary>
    public static int? LaunchUpdater(string updaterExe, string jobPath, UpdateJob job, bool elevated) =>
        LaunchCore(updaterExe, new List<(string Name, string Value)>
        {
            ("--job", jobPath),
            ("--url", job.Download.Url ?? ""),
            ("--hash", job.Download.Sha256 ?? ""),
            ("--target", job.App.InstallDir ?? ""),
            ("--exe", job.App.ExeName ?? ""),
        }, elevated);

    /// <summary>
    /// 用四个显式参数拉起**已经准备好**的更新器（<c>%Temp%\CDL_UpdateTemp\Updater.exe</c>）。
    ///
    /// 这条路径不写 job.json：检查更新时更新器就已经复制到 temp 了，
    /// 点「立即更新」只剩"启动它"——所以窗口是秒开的。job.json 那条慢路径保留为兜底。
    /// </summary>
    public static int? LaunchUpdater(string updaterExe, string url, string sha256,
                                     string installDir, string exeName, bool elevated) =>
        LaunchCore(updaterExe, new List<(string Name, string Value)>
        {
            ("--url", url ?? ""),
            ("--hash", sha256 ?? ""),
            ("--target", installDir ?? ""),
            ("--exe", exeName ?? ""),
        }, elevated);

    /// <summary>公共部分：提权走 runas（UseShellExecute + 手工引号），否则用 ArgumentList。</summary>
    private static int? LaunchCore(string updaterExe, List<(string Name, string Value)> arguments, bool elevated)
    {
        var info = new ProcessStartInfo
        {
            FileName = updaterExe,
            WorkingDirectory = Path.GetDirectoryName(updaterExe) ?? "",
        };

        if (elevated)
        {
            // runas 必须 UseShellExecute=true，而 ArgumentList 与它互斥（框架会抛），
            // 只能手工加引号 —— 路径里有中文和空格（"Class Daily Land 2"）是常态。
            info.UseShellExecute = true;
            info.Verb = "runas";
            info.Arguments = string.Join(' ', arguments.Select(a => $"{a.Name} \"{a.Value}\""));
        }
        else
        {
            // 不提权时用 ArgumentList：省掉手工转义，路径里的空格和中文原样过去
            info.UseShellExecute = false;
            info.CreateNoWindow = true;

            foreach (var (name, value) in arguments)
            {
                info.ArgumentList.Add(name);
                info.ArgumentList.Add(value);
            }
        }

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