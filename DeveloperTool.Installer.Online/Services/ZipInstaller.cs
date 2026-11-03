using System.IO.Compression;
using ClassDailyLand2.UpdateContract;

namespace DeveloperTool.Installer.Online.Services;

/// <summary>
/// 解压 IDE 整包到安装目录。
///
/// 三个刻意的选择：
///   · 不用 <c>ZipFile.ExtractToDirectory</c> 一把梭：它既不给逐条目进度，
///     也没法在"条目名带 .."时整包拒绝（zip slip）。这里走 <see cref="ZipArchive"/> 逐条落地。
///   · 路径校验复用更新契约的 <see cref="SafeRelativePath"/>，与更新器同一套规则；
///   · <c>projects\</c> / <c>plugins\</c> / <c>Temp\</c> / <c>WebView2Data\</c> 与
///     <c>ide_config.json</c> / <c>ide_auth.json</c> / <c>recent_projects.json</c> **永不覆盖**
///     —— 用户重装/升级时项目、插件、登录态不能被清掉。
/// </summary>
internal static class ZipInstaller
{
    // 保留目录：都在安装根下，按首段目录名整棵跳过
    private static readonly string[] PreservedDirs = { "projects", "plugins", "Temp", "WebView2Data" };

    // 保留文件：精确文件名（配置/登录态）+ 运行痕迹通配。文件名在任意层级都算
    private static readonly string[] PreservedPatterns =
    {
        "ide_config.json", "ide_auth.json", "recent_projects.json", "*.log", "*.pdb", "*.bak-*",
    };

    private const long MaxTotalBytes = 512L * 1024 * 1024;

    public static async Task<int> ExtractAsync(string zipPath, string targetDir,
                                               IProgress<DownloadProgress>? progress,
                                               double fromPercent, double toPercent,
                                               CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        using var archive = ZipFile.OpenRead(zipPath);

        var files = new List<(string Relative, ZipArchiveEntry Entry)>();

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            if (entry.Name.Length == 0) continue;

            if (!SafeRelativePath.TryNormalize(entry.FullName, out var relative, out var why))
                throw new InstallFailedException($"安装包里有非法路径「{entry.FullName}」：{why}");

            if (IsPreserved(relative)) continue;

            files.Add((relative, entry));
        }

        if (files.Count == 0)
            throw new InstallFailedException("安装包里没有任何可解压的文件。");

        long total = files.Sum(f => Math.Max(0, f.Entry.Length));
        if (total > MaxTotalBytes)
            throw new InstallFailedException($"安装包解压后过大（{total / 1048576} MB），疑似异常包。");

        long written = 0;
        var index = 0;

        foreach (var (relative, entry) in files)
        {
            ct.ThrowIfCancellationRequested();

            var destination = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));

            // 第二道校验：归一化之后仍必须落在安装目录内
            if (!SafeRelativePath.IsInside(targetDir, destination))
                throw new InstallFailedException($"安装包条目越界：{entry.FullName}");

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            await using (var source = entry.Open())
            await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write,
                                                   FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            written += Math.Max(0, entry.Length);
            index++;

            var percent = fromPercent + (toPercent - fromPercent) * ((double)index / files.Count);
            progress?.Report(new DownloadProgress(percent, $"解压中 {index} / {files.Count} 个文件"));

            // 让 UI 有机会刷新（解压是 CPU/IO 密集，纯 await 已足够，这里只是显式表达意图）
            if (index % 64 == 0) await Task.Yield();
        }

        _ = written;
        return files.Count;
    }

    /// <summary>
    /// 两级判定：目录名（只比首段，保留目录都在安装根下）与文件名（任意层级，含通配）。
    /// </summary>
    private static bool IsPreserved(string relative)
    {
        var normalized = SafeRelativePath.Normalize(relative);
        if (normalized.Length == 0) return false;

        var slash = normalized.IndexOf('/');
        var first = slash < 0 ? normalized : normalized[..slash];

        foreach (var dir in PreservedDirs)
        {
            if (string.Equals(first, dir, StringComparison.OrdinalIgnoreCase)) return true;
        }

        var leaf = slash < 0 ? normalized : normalized[(normalized.LastIndexOf('/') + 1)..];
        foreach (var pattern in PreservedPatterns)
        {
            if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, leaf, ignoreCase: true))
                return true;
        }

        return false;
    }
}
