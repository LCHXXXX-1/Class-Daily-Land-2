using System.IO.Compression;
using ClassDailyLand.UpdateContract;

namespace ClassDailyLand.Updater;

/// <summary>
/// 安全解压。与主程序 <c>PluginInstaller.ExtractSafely</c> 同一套防线，但目标是**程序目录**，
/// 所以校验更严（条目数 / 体积上限都更保守）。
/// </summary>
public static class SafeZip
{
    /// <summary>
    /// 解压到 <paramref name="destDir"/>，返回落地的相对路径列表。
    /// 任何条目越界、超限 → 整包拒绝并清空已解压内容（不留下半套）。
    /// </summary>
    public static (List<string> Files, string? Error) Extract(
        string zipPath, string destDir, int maxEntries = 5000, long maxBytes = 512L * 1024 * 1024)
    {
        var files = new List<string>();

        if (!File.Exists(zipPath))
            return (files, $"安装包不存在：{zipPath}");

        try
        {
            Directory.CreateDirectory(destDir);
            var root = Path.GetFullPath(destDir);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(zipPath);

            long totalBytes = 0;
            var count = 0;

            foreach (var entry in archive.Entries)
            {
                if (++count > maxEntries)
                {
                    Directory.Delete(destDir, recursive: true);
                    return (files, $"包内文件超过 {maxEntries} 个，拒绝解压（可能是解压炸弹）");
                }

                // 目录条目（"foo/"、"./"、"."）没有内容，直接跳过。
                // ★ tar/bsdtar 打的包必带一个 "./" 根条目：它归一化后是空串，
                //   Path.Combine(root, "") 得到不带分隔符的根目录，根前缀校验会误报"越界"。
                //   文件条目解压时会自动创建所需目录，不需要显式处理目录条目。
                var rawName = entry.FullName.Replace('\\', '/');
                if (rawName.EndsWith('/') || rawName is "." or "./" || entry.Name.Length == 0)
                    continue;

                if (!SafeRelativePath.TryNormalize(entry.FullName, out var relative, out var why))
                {
                    Directory.Delete(destDir, recursive: true);
                    return (files, $"包内路径不安全：「{entry.FullName}」（{why}）");
                }

                var dest = Path.GetFullPath(Path.Combine(root, relative));
                if (!SafeRelativePath.IsInside(root, dest))
                {
                    Directory.Delete(destDir, recursive: true);
                    return (files, $"包内路径越界：「{entry.FullName}」");
                }

                if (entry.FullName.EndsWith('/') || entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }

                totalBytes += entry.Length;
                if (totalBytes > maxBytes)
                {
                    Directory.Delete(destDir, recursive: true);
                    return (files, $"包内体积超过 {maxBytes / 1024 / 1024}MB，拒绝解压");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                files.Add(relative);
            }

            if (files.Count == 0)
            {
                Directory.Delete(destDir, recursive: true);
                return (files, "包里没有文件");
            }

            return (files, null);
        }
        catch (InvalidDataException)
        {
            try { Directory.Delete(destDir, recursive: true); } catch { }
            return (files, "安装包已损坏或不是 zip 格式");
        }
        catch (IOException ex)
        {
            try { Directory.Delete(destDir, recursive: true); } catch { }
            return (files, "解压失败：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            try { Directory.Delete(destDir, recursive: true); } catch { }
            return (files, "没有权限解压到目标位置：" + ex.Message);
        }
        catch (Exception ex)
        {
            try { Directory.Delete(destDir, recursive: true); } catch { }
            return (files, $"解压异常：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>包根判定：条目要么在根目录，要么在唯一一级子目录里（对齐 PluginInstaller 的规则）。</summary>
    public static string? ResolvePayloadRoot(string extractedDir)
    {
        if (File.Exists(Path.Combine(extractedDir, "install.info.json"))) return extractedDir;

        // 只有一层子目录且里面有东西 → 以它为包根（从 GitHub 打的包常套一层）
        var dirs = Directory.GetDirectories(extractedDir);
        var hasFiles = Directory.EnumerateFiles(extractedDir).Any();
        if (!hasFiles && dirs.Length == 1) return dirs[0];

        return extractedDir;
    }
}
