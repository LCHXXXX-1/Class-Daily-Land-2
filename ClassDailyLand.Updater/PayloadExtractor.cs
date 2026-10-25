using System.IO.Compression;
using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.Updater;

/// <summary>
/// 解压安装包到 **staging 目录**（不是直接铺到程序目录上）。
///
/// 先解到 staging 有两个好处：
///   1. 能先拿到完整的 payload 清单，再算出「该覆盖谁 / 该删谁 / 谁绝不动」的替换计划；
///   2. 包坏了（CRC 错、路径穿越、条目过多）在动程序目录之前就能整包拒绝。
///
/// 三道闸门都来自契约，不在更新器里重写：
///   <see cref="SafeRelativePath"/>（Zip Slip）、<see cref="PreserveRules"/>（settings / plugins 绝不落地）、
///   <see cref="JobLimits"/>（条目数 / 解压总量，防 zip 炸弹）。
/// </summary>
internal static class PayloadExtractor
{
    /// <summary>返回将要落地的相对路径清单；null 表示整包拒绝（<paramref name="error"/> 为人话原因）。</summary>
    public static List<string>? Extract(string zipPath, string stagingDir, UpdateJob job, JobLog log,
                                        out string? error, Action<int, int>? progress = null)
    {
        error = null;
        var payload = new List<string>();

        try
        {
            Directory.CreateDirectory(stagingDir);

            using var archive = ZipFile.OpenRead(zipPath);

            if (archive.Entries.Count > job.Limits.MaxEntries)
            {
                error = $"安装包条目过多：{archive.Entries.Count} 个（上限 {job.Limits.MaxEntries}）";
                return null;
            }

            long total = 0;

            foreach (var entry in archive.Entries)
            {
                // 目录条目（".../") 不落地：目录由下面的 CreateDirectory 按需建
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (entry.Name.Length == 0) continue;

                if (!SafeRelativePath.TryNormalize(entry.FullName, out var relative, out var why))
                {
                    error = $"安装包里含有非法路径「{entry.FullName}」：{why}";
                    return null;
                }

                if (PreserveRules.IsPreserved(relative, job.Preserve, job.PreservePatterns))
                {
                    // 极端事故：打包时把 settings / plugins 打进来了。绝不落地，但要留痕。
                    log.Write($"跳过保留项（不进程序目录）：{relative}");
                    continue;
                }

                total += entry.Length;
                if (total > job.Limits.MaxTotalBytes)
                {
                    error = $"安装包解压后超过上限（{job.Limits.MaxTotalBytes} 字节），疑似异常包";
                    return null;
                }

                var dest = Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar));

                // 第二道校验：归一化之后仍要保证落在 staging 之内
                if (!SafeRelativePath.IsInside(stagingDir, dest))
                {
                    error = $"安装包条目越界（归一化后仍在目录外）：{entry.FullName}";
                    return null;
                }

                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                entry.ExtractToFile(dest, overwrite: true);
                payload.Add(relative);

                // 每落一个文件报一次（总量用条目数近似，够进度条用了）
                progress?.Invoke(payload.Count, archive.Entries.Count);
            }
        }
        catch (InvalidDataException ex)
        {
            // 压缩包损坏 / CRC 不匹配
            error = "安装包已损坏：" + ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "解压失败：" + ex.Message;
            return null;
        }

        if (payload.Count == 0)
        {
            error = "安装包里没有任何可落地的文件";
            return null;
        }

        payload.Sort(StringComparer.OrdinalIgnoreCase);
        return payload;
    }
}
