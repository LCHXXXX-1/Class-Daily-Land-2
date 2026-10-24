using System.IO.Compression;

namespace ClassDailyLand.Core.Plugins;

/// <summary>安装 / 卸载结果。<see cref="Error"/> 非空时 <see cref="Ok"/> 必为 false。</summary>
public sealed record InstallOutcome(bool Ok, string? PluginId, string? Error, string? InstalledPath = null)
{
    public static InstallOutcome Fail(string error) => new(false, null, error);
    public static InstallOutcome Good(string id, string path) => new(true, id, null, path);
}

/// <summary>
/// 插件安装与卸载。
///
/// 支持的来源：
///   · <c>.cblplugin</c> / <c>.zip</c> 压缩包（plugin.json 在根，或在唯一的一级子目录里）
///   · 一个本地文件夹（开发时直接把输出目录发过来）
///
/// 安全约束（都是实打实的检查，不是注释）：
///   · 解压逐条校验落点必须在临时目录内，挡掉 <c>../</c> 与绝对路径
///   · 限制条目数与总字节数，挡掉解压炸弹
///   · 安装目录名取清单里的 id，且 id 只能含字母数字与 . _ -
///   · 覆盖安装先把旧目录改名备份，成功了才真删；中途失败会还原
/// </summary>
public static class PluginInstaller
{
    public const string PackageExtension = ".cblplugin";
    const int MaxEntries = 2000;
    const long MaxTotalBytes = 64L * 1024 * 1024;

    // ————————————————————— 安装 —————————————————————

    public static InstallOutcome Install(string sourcePath, string pluginsRoot, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return InstallOutcome.Fail("没有指定要安装的文件或文件夹。");
        if (!Directory.Exists(pluginsRoot))
        {
            try { Directory.CreateDirectory(pluginsRoot); }
            catch (Exception ex) { return InstallOutcome.Fail("创建插件目录失败：" + ex.Message); }
        }

        try
        {
            if (Directory.Exists(sourcePath))
                return InstallFromFolder(sourcePath, pluginsRoot, overwrite);
            if (File.Exists(sourcePath))
                return InstallFromPackage(sourcePath, pluginsRoot, overwrite);
            return InstallOutcome.Fail("路径不存在：" + sourcePath);
        }
        catch (Exception ex)
        {
            return InstallOutcome.Fail("安装失败：" + ex.Message);
        }
    }

    static InstallOutcome InstallFromPackage(string packagePath, string pluginsRoot, bool overwrite)
    {
        var ext = Path.GetExtension(packagePath);
        if (!ext.Equals(PackageExtension, StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return InstallOutcome.Fail($"不认识的包类型「{ext}」，请用 {PackageExtension} 或 .zip。");

        var sandbox = Path.Combine(pluginsRoot, "._install_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);
            var extracted = ExtractSafely(packagePath, sandbox, out var error);
            if (extracted is null) return InstallOutcome.Fail(error ?? "解压失败。");

            // 插件根：解压目录本身，或唯一的一级子目录（打 zip 时多套了一层文件夹很常见）
            var root = extracted;
            if (!PluginCatalog.LooksLikePlugin(root))
            {
                var children = Directory.GetDirectories(root);
                if (children.Length == 1 && PluginCatalog.LooksLikePlugin(children[0])) root = children[0];
                else return InstallOutcome.Fail("包里找不到 plugin.json（应在压缩包根目录或唯一子目录下）。");
            }

            return PlaceInto(root, pluginsRoot, overwrite);
        }
        finally
        {
            TryDeleteDirectory(sandbox);
        }
    }

    static InstallOutcome InstallFromFolder(string folder, string pluginsRoot, bool overwrite)
    {
        if (!PluginCatalog.LooksLikePlugin(folder))
        {
            // 允许传上一层（比如 bin/Debug/net10.0/），自动往下找一层
            var children = Directory.GetDirectories(folder)
                .Where(PluginCatalog.LooksLikePlugin).ToList();
            if (children.Count != 1)
                return InstallOutcome.Fail("这个文件夹里没有 plugin.json。");
            folder = children[0];
        }
        return PlaceInto(folder, pluginsRoot, overwrite);
    }

    /// <summary>把已解压 / 已定位好的插件目录搬到 plugins/&lt;id&gt;/。</summary>
    static InstallOutcome PlaceInto(string sourceDir, string pluginsRoot, bool overwrite)
    {
        if (!PluginManifest.TryLoad(sourceDir, out var manifest, out var error))
            return InstallOutcome.Fail(error ?? "插件清单不合法。");

        var target = Path.Combine(pluginsRoot, manifest.Id);
        var backup = target + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        var hadOld = Directory.Exists(target);

        if (hadOld && !overwrite)
            // 带上 id：界面要按它问"用新包覆盖吗"
            return new InstallOutcome(false, manifest.Id,
                $"已经装过「{manifest.Id}」了。要升级请覆盖安装，或先卸载。");

        try
        {
            if (hadOld) Directory.Move(target, backup);
            CopyDirectory(sourceDir, target);
        }
        catch (Exception ex)
        {
            // 还原：宁可回到装之前的样子，也不要留一个半截目录
            TryDeleteDirectory(target);
            if (hadOld && Directory.Exists(backup))
            {
                try { Directory.Move(backup, target); } catch { /* 还原失败只能放弃 */ }
            }
            return InstallOutcome.Fail("写入插件目录失败：" + ex.Message);
        }

        if (hadOld) TryDeleteDirectory(backup);
        return InstallOutcome.Good(manifest.Id, target);
    }

    // ————————————————————— 卸载 —————————————————————

    public static InstallOutcome Uninstall(string pluginId, string pluginsRoot)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            return InstallOutcome.Fail("没有指定插件 id。");

        var id = pluginId.Trim();
        if (id.Contains('/') || id.Contains('\\') || id.Contains(".."))
            return InstallOutcome.Fail($"插件 id「{id}」不合法。");

        var target = Path.Combine(pluginsRoot, id);
        if (!Directory.Exists(target))
            return InstallOutcome.Fail($"没有找到插件目录：{target}");

        try
        {
            Directory.Delete(target, recursive: true);
            return InstallOutcome.Good(id, target);
        }
        catch (Exception ex)
        {
            return InstallOutcome.Fail("删除失败（可能文件被占用，先禁用或重启后重试）：" + ex.Message);
        }
    }

    // ————————————————————— 解压（带安全校验） —————————————————————

    /// <summary>返回实际解压目录；失败返回 null。</summary>
    static string? ExtractSafely(string packagePath, string sandbox, out string? error)
    {
        error = null;
        var root = Path.GetFullPath(sandbox) + Path.DirectorySeparatorChar;

        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            long total = 0;
            var count = 0;

            foreach (var entry in zip.Entries)
            {
                if (++count > MaxEntries)
                {
                    error = $"包内文件超过 {MaxEntries} 个，已拒绝安装。";
                    return null;
                }

                var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (relative.Length == 0) continue;

                var dest = Path.GetFullPath(Path.Combine(sandbox, relative));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"包内路径越界：「{entry.FullName}」，已拒绝安装。";
                    return null;
                }

                if (entry.FullName.EndsWith('/') || entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }

                total += entry.Length;
                if (total > MaxTotalBytes)
                {
                    error = "包解压后超过 64 MB，已拒绝安装。";
                    return null;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
            return sandbox;
        }
        catch (InvalidDataException)
        {
            error = "压缩包已损坏或不是 zip 格式。";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "解压失败：" + ex.Message;
            return null;
        }
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            // data/ 是插件运行期数据，升级时不要覆盖用户已有的
            Directory.CreateDirectory(Path.Combine(target, rel));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            if (File.Exists(dest) && rel.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* 清理失败不影响主流程 */ }
    }
}
