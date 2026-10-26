using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClassDailyLand2.Core;

namespace DeveloperIDE;

/// <summary>
/// <c>config.json</c>（exe 旁）的读取结果：应用名、更新源、仓库地址。
///
/// 读取一律**容错**：文件缺失、字段缺失、JSON 写坏都退回默认值 ——
/// 关于页和版本检查不能因为一份坏配置就让 IDE 起不来。
/// </summary>
public sealed record AppConfig(
    string AppName,
    string Version,
    string ExeName,
    string VersionUrl,
    string VersionUrlFallback,
    string HomeUrl,
    string HomeUrlFallback)
{
    public const string FileName = "config.json";

    public const string DefaultAppName = "Class Daily Land 2 插件 IDE";

    // 主源用 jsDelivr：Gitee 的 raw 链接会 302 到鉴权域名，HttpClient 跟着跳经常卡到超时。
    // jsDelivr 直接 200 返回内容，国内访问也稳；备用源走 GitHub raw 的 developerIDE 分支。
    public const string DefaultVersionUrl =
        "https://cdn.jsdelivr.net/gh/LCHXXXX-1/Class-Daily-Land-2@developerIDE/version.json";
    public const string DefaultVersionUrlFallback =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/developerIDE/version.json";
    public const string DefaultHomeUrl = "https://gitee.com/lchxxxx/class-daily-land-2";
    public const string DefaultHomeUrlFallback = "https://github.com/LCHXXXX-1/Class-Daily-Land-2";

    /// <summary>配置文件的落点（输出目录根，csproj 里 PreserveNewest 复制过来）。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>版本号唯一真源是主程序的 <c>Updates\VersionInfo.cs</c>，这里直接读常量。</summary>
    public static string AssemblyVersion => VersionInfo.Version;

    public static AppConfig Load()
    {
        var appName = DefaultAppName;
        var version = AssemblyVersion;
        var exeName = "DeveloperIDE.exe";
        var versionUrl = DefaultVersionUrl;
        var versionUrlFallback = DefaultVersionUrlFallback;
        var homeUrl = DefaultHomeUrl;
        var homeUrlFallback = DefaultHomeUrlFallback;

        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                var root = doc.RootElement;

                appName = Text(root, "app_name", appName);
                version = Text(root, "version", version);
                exeName = Text(root, "exe_name", exeName);
                versionUrl = ToRawUrl(Text(root, "version_url", versionUrl));
                versionUrlFallback = ToRawUrl(Text(root, "version_url_fallback", versionUrlFallback));
                homeUrl = Text(root, "home_url", homeUrl);
                homeUrlFallback = Text(root, "home_url_fallback", homeUrlFallback);
            }
        }
        catch
        {
            // 坏配置按默认值继续，调用方不需要处理异常
        }

        // 兜底源没配或写错就复用主源，至少界面不会显示空白
        if (string.IsNullOrWhiteSpace(homeUrlFallback)) homeUrlFallback = homeUrl;

        return new AppConfig(appName, version, exeName, versionUrl, versionUrlFallback, homeUrl, homeUrlFallback);
    }

    static string Text(JsonElement root, string key, string fallback) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : fallback;

    /// <summary>
    /// 浏览器地址转成程序能读的原始地址：直接粘 <c>.../blob/分支/version.json</c> 是最常见的用法，
    /// 但那种链接返回的是网页 HTML，JsonDocument 解析不了，检查更新会一直失败。
    /// Gitee 同域名换段（blob → raw）；GitHub 换成 raw.githubusercontent.com。
    /// </summary>
    public static string ToRawUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        var text = url.Trim();

        if (text.Contains("gitee.com", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(text, @"/blob/", "/raw/", RegexOptions.IgnoreCase);
        }

        if (text.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            text = Regex.Replace(text, @"^https://github\.com/", "https://raw.githubusercontent.com/",
                RegexOptions.IgnoreCase);
            return Regex.Replace(text, @"/blob/", "/", RegexOptions.IgnoreCase);
        }

        return text;
    }

    /// <summary>仓库地址取第一个能用的（Gitee 优先，GitHub 兜底）。</summary>
    public string PrimaryHomeUrl => string.IsNullOrWhiteSpace(HomeUrl) ? HomeUrlFallback : HomeUrl;
}
