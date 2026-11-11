using ClassDailyLand2.UpdateContract;

namespace ClassDailyLand2.Updater;

/// <summary>
/// 把命令行变成一份 <see cref="UpdateJob"/>：有 <c>--job</c> 就读文件，否则用四个显式参数合成一份。
/// 两条路最后都经过 <see cref="UpdateJob.Normalize"/>，保证下游只面对一种形状。
/// </summary>
internal static class JobLoader
{
    private const string DefaultExeName = "Class Daily Land 2.exe";

    public static UpdateJob? Load(CommandLine cli, out string? error)
    {
        error = null;

        if (cli.HasJob)
        {
            var path = cli.JobPath!;
            var job = UpdateJson.Read<UpdateJob>(path);
            if (job is null)
            {
                error = $"读不了更新任务文件：{path}（文件不存在或不是合法 JSON）";
                return null;
            }

            if (!job.IsSchemaSupported)
            {
                // 高于自己的 schema 说明"更新器比主程序旧"，猜字段会把程序弄坏
                error = $"任务文件 schema {job.Schema} 高于本更新器支持的 {UpdateJob.CurrentSchema}，拒绝执行";
                return null;
            }

            job.Normalize();

            if (string.IsNullOrWhiteSpace(job.Download.Url))
            {
                error = "任务文件里没有下载地址";
                return null;
            }
            if (string.IsNullOrWhiteSpace(job.App.InstallDir) || !Directory.Exists(job.App.InstallDir))
            {
                error = $"任务文件里的安装目录不存在：{job.App.InstallDir}";
                return null;
            }

            return job;
        }

        // 四个参数的路：--url 必需，--target 必需（能同时兼容手动救援）
        if (!cli.HasUrl)
        {
            error = "缺少 --url（或改用 --job 指定任务文件）";
            return null;
        }
        if (string.IsNullOrWhiteSpace(cli.TargetDir))
        {
            error = "缺少 --target（主程序目录）";
            return null;
        }

        string target;
        try { target = Path.GetFullPath(cli.TargetDir!); }
        catch (Exception ex)
        {
            error = $"目标目录不合法：{ex.Message}";
            return null;
        }

        if (!Directory.Exists(target))
        {
            error = $"目标目录不存在：{target}";
            return null;
        }

        var synthesized = new UpdateJob
        {
            JobId = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            Action = JobAction.Update,
            App = new JobApp
            {
                ExeName = string.IsNullOrWhiteSpace(cli.ExeName) ? DefaultExeName : cli.ExeName!.Trim(),
                InstallDir = target,
                Pid = 0,
            },
            Download = new JobDownload
            {
                Url = cli.Url!.Trim(),
                UrlFallback = cli.UrlFallback?.Trim() ?? "",
                Sha256 = cli.Sha256?.Trim() ?? "",
            },
        };

        synthesized.Normalize();
        return synthesized;
    }
}
