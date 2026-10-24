using System.Text.Json;

namespace ClassDailyLand.App.Updates;

/// <summary>
/// <c>Resources\config.json</c> 的读取结果（类名 <see cref="AppConfig"/>，文件名沿用 config 便于对照）。
///
/// 这个文件是随包发布的**部署配置**（跟哪个源要版本、程序叫什么名字），
/// 不再是"版本号本体"—— 版本号已经搬到 <c>Directory.Build.props</c>，
/// 留一份写死的 <c>version</c> 只会和程序集对不上，那正是之前的 bug。
/// </summary>
public sealed record AppConfig(
    string VersionUrl,
    string VersionUrlFallback,
    string ExeName,
    string UpdaterDir,
    string AssetsDir)
{
    public const string DefaultVersionUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/update/version.json";
    public const string DefaultVersionUrlFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/update/version.json";
    public const string DefaultExeName = "Class Daily Land.exe";
    public const string DefaultUpdaterDir = "updater";
    public const string DefaultAssetsDir = "assets";

    /// <summary>主源与兜底源都不空时，返回「先试主源，失败再试兜底」的有序列表（去重、去空）。</summary>
    public IReadOnlyList<string> VersionUrls
    {
        get
        {
            var list = new List<string>();
            foreach (var u in new[] { VersionUrl, VersionUrlFallback })
            {
                if (string.IsNullOrWhiteSpace(u)) continue;
                if (list.Any(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(u.Trim());
            }
            return list;
        }
    }

    /// <summary>程序目录下的配置文件位置。</summary>
    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "config.json");

    /// <summary>
    /// 读取。文件缺失、损坏、字段缺失都**不报错**，用默认值顶上 ——
    /// 更新功能挂了不能反过来把主程序打不开。
    /// </summary>
    public static AppConfig Load()
    {
        var url = DefaultVersionUrl;
        var urlFallback = DefaultVersionUrlFallback;
        var exeName = DefaultExeName;
        var updaterDir = DefaultUpdaterDir;
        var assetsDir = DefaultAssetsDir;

        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                var root = doc.RootElement;

                if (root.TryGetProperty("version_url", out var v) && IsHttp(v.GetString()))
                    url = v.GetString()!.Trim();

                // 兜底源字段是新加的：旧包里可能没有
                if (root.TryGetProperty("version_url_fallback", out var f) && IsHttp(f.GetString()))
                    urlFallback = f.GetString()!.Trim();

                if (root.TryGetProperty("exe_name", out var e) && !string.IsNullOrWhiteSpace(e.GetString()))
                    exeName = e.GetString()!.Trim();

                if (root.TryGetProperty("updater_dir", out var u) && !string.IsNullOrWhiteSpace(u.GetString()))
                    updaterDir = u.GetString()!.Trim();

                if (root.TryGetProperty("assets_dir", out var a) && !string.IsNullOrWhiteSpace(a.GetString()))
                    assetsDir = a.GetString()!.Trim();
            }
        }
        catch
        {
            // 配置文件坏掉时用默认值继续跑；下面会写日志（调用方负责）
        }

        // 兜底源没配或写错了就复用主源（至少能重试一次）
        if (!IsHttp(urlFallback) || string.Equals(url, urlFallback, StringComparison.OrdinalIgnoreCase))
            urlFallback = url;

        return new AppConfig(url, urlFallback, exeName, updaterDir, assetsDir);
    }

    static bool IsHttp(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
