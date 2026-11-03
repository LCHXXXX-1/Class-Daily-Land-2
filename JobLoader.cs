using ClassDailyLand2.UpdateContract;

namespace CDL2.Updater;

/// <summary>
/// 把命令行变成一份 <see cref="UpdateJob"/>。有 <c>--job</c> 读文件，否则用显式参数合成；
/// 两条路最后都过 <see cref="UpdateJob.Normalize"/>，下游只面对一种形状。
/// </summary>
public static class JobLoader
{
    private const string DefaultExeName = "Class Daily Land 2.exe";

    public static UpdateJob? Load(CommandLine cli, out string? error)
    {
        error = null;

        if (cli.HasJob)
        {
            var path = cli.JobPath!;
            UpdateJob? job;
            try
            {
                job = UpdateJson.Read<UpdateJob>(path);
            }
            catch (Exception ex)
            {
                error = $"读更新任务文件失败：{path}（{ex.Message}）";
                return null;
            }

            if (job is null)
            {
                error = $"读不了更新任务文件：{path}（文件不存在或不是合法 JSON）";
                return null;
            }

            if (!job.IsSchemaSupported)
            {
                error = $"任务文件 schema {job.Schema} 高于本更新器支持的 {UpdateJob.CurrentSchema}，请先更新更新器";
                return null;
            }

            job.Normalize();

            if (!string.Equals(job.Action, JobAction.Update, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(job.Action, JobAction.Resources, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(job.Action, JobAction.Rollback, StringComparison.OrdinalIgnoreCase))
            {
                error = $"未知的任务动作：{job.Action}";
                return null;
            }

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

        // 手工救援路：--url 与 --target 必需
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
        try { target = System.IO.Path.GetFullPath(cli.TargetDir!); }
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
