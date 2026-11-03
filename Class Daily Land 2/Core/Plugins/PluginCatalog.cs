namespace ClassDailyLand2.Core.Plugins;

/// <summary>
/// 扫描到的一个插件目录。<see cref="Error"/> 非空表示这个目录有问题（缺清单 / 清单非法 /
/// 入口缺失 / id 重复），设置中心要把它列出来并显示原因 —— 静默跳过是最难排查的。
/// </summary>
public sealed record PluginPackage(
    string Id,
    string Directory,
    PluginManifest? Manifest,
    string? Error)
{
    public bool Ok => Error is null && Manifest is not null;
    public string Name => Manifest?.Name ?? Path.GetFileName(Directory);
    public string Version => Manifest?.Version ?? "-";
}

/// <summary>插件目录扫描：只读文件系统，不加载任何程序集。</summary>
public static class PluginCatalog
{
    const string ManifestFile = "plugin.json";

    /// <summary>
    /// 扫 <paramref name="pluginsRoot"/> 下的一级子目录。
    /// 结果为「按名称排序」的稳定顺序，便于 UI 与日志对照。
    /// </summary>
    public static IReadOnlyList<PluginPackage> Scan(string pluginsRoot)
    {
        var result = new List<PluginPackage>();
        if (string.IsNullOrWhiteSpace(pluginsRoot) || !Directory.Exists(pluginsRoot))
            return result;

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(pluginsRoot);
        }
        catch (Exception)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(dir);
            // 隐藏目录与临时目录（. 或 _ 开头）不参与扫描
            if (folder.StartsWith('.') || folder.StartsWith('_'))
            {
                Logger.Current?.Trace($"[插件] 跳过隐藏/临时目录 {dir}");
                continue;
            }

            // 覆盖安装留下的备份目录（<id>.bak-时间戳）：不是插件，别把它列成"加载失败"
            if (IsLeftover(folder))
            {
                Logger.Current?.Trace($"[插件] 跳过覆盖安装留下的备份目录 {dir}");
                continue;
            }

            if (!PluginManifest.TryLoad(dir, out var manifest, out var error))
            {
                // 缺清单是最常见的"插件目录里东西不见了"，说清是哪个目录、缺什么文件
                Logger.Current?.Warn($"[插件] {dir} 无法作为插件加载：{error ?? "无法加载"}");
                result.Add(new PluginPackage(folder, dir, null, error ?? "无法加载。"));
                continue;
            }

            // id 与目录名不一致不算错误（安装时会以 id 命名目录），但如果撞 id 必须拦一个
            if (!seen.Add(manifest.Id))
            {
                Logger.Current?.Warn($"[插件] {dir} 的 id「{manifest.Id}」与另一个插件重复，已忽略这一个");
                result.Add(new PluginPackage(manifest.Id, dir, null,
                    $"id「{manifest.Id}」与另一个插件重复，已忽略这一个。"));
                continue;
            }

            Logger.Current?.Trace($"[插件] 识别到 {manifest.Id} @ {dir}（v{manifest.Version}，入口 {manifest.Entry}）");

            result.Add(new PluginPackage(manifest.Id, dir, manifest, null));
        }

        return result;
    }

    /// <summary>目录里是否有 plugin.json（安装时判断解压出来的是不是插件包）。</summary>
    public static bool LooksLikePlugin(string directory) =>
        Directory.Exists(directory) && File.Exists(Path.Combine(directory, ManifestFile));

    /// <summary>
    /// 是不是「留下的垃圾目录」：覆盖安装的备份（<c>&lt;id&gt;.bak-20261101120000</c>）
    /// 与卸载失败的待删目录（<c>_trash-xxxx</c>）。
    ///
    /// <para>它们不该出现在插件列表里 —— 备份目录缺 plugin.json，会被渲染成
    /// 「加载失败：缺少 plugin.json」；用户点卸载时，旧 dll 的句柄往往还没释放，
    /// 于是删不掉，观感就成了"这东西卸不掉"。</para>
    /// </summary>
    public static bool IsLeftover(string folderName) =>
        !string.IsNullOrEmpty(folderName) &&
        (folderName.Contains(".bak-", StringComparison.OrdinalIgnoreCase) ||
         folderName.StartsWith("_trash-", StringComparison.OrdinalIgnoreCase));
}
