using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ClassDailyLand2.Core;

namespace DeveloperIDE;

/// <summary>
/// 更新链路的公共部分：拉 version.json、比版本、把同目录的 IDEUpdater.exe 复制到 temp 再拉起。
///
/// AboutWindow（手动检查）和 MainWindow（启动静默检查）共用这一份，
/// 免得两边的参数拼法哪天走岔了 —— 拼错 <c>--target</c> 是会覆盖错目录的。
/// </summary>
public static class UpdateLauncher
{
    /// <summary>
    /// 主地址走 jsDelivr CDN：Gitee 的 raw 链接会 302 跳到鉴权域名，客户端跟随后经常卡到超时；
    /// jsDelivr 直接 200 返回内容，国内也稳。分支用 @developerIDE 锁死，免得和主程序的 update 分支串版本。
    /// </summary>
    public const string VersionUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/raw/developerIDE/version.json";

    /// <summary>备用源：GitHub raw（config.json 里没配 version_url_fallback 时用它）。</summary>
    public const string DefaultFallbackUrl =
        "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/developerIDE/version.json";

    const string TempUpdaterName = "IDEUpdater_Temp.exe";
    const string MainExeName = "DeveloperIDE.exe";
    const string UpdaterExeName = "IDEUpdater.exe";

    /// <summary>
    /// 伪装成浏览器：Gitee 这类站点对「不像浏览器」的抓取请求会限速或挂起 ——
    /// 浏览器能秒开而程序超时，通常就是这里 + 没走系统代理。
    /// </summary>
    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>
    /// 静态单例：HttpClient 每 new 一次就建一套连接池，频繁检查会把端口耗光（TIME_WAIT 堆积）。
    /// 配置与浏览器对齐：系统代理、10 秒超时、Chrome UA + Accept。
    /// </summary>
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        HttpClientHandler handler;
        try
        {
            // WebRequest 在 .NET 5+ 标了过时（SYSLIB0014），但显式取系统代理最稳：
            // 公司代理与 PAC 脚本都挂在这里，光靠 UseProxy=true 有时拿不到。
#pragma warning disable SYSLIB0014
            handler = new HttpClientHandler
            {
                UseProxy = true,
                // 重定向必须自动跟：raw / CDN 域名之间会 302，手动跟还得自己写超时
                AllowAutoRedirect = true,
                Proxy = WebRequest.GetSystemWebProxy(),
                Credentials = CredentialCache.DefaultNetworkCredentials,
            };
#pragma warning restore SYSLIB0014
        }
        catch
        {
            // 极少数环境（组策略限制 / API 不支持）取不到系统代理，退回默认实现
            handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = true };
        }

        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        // 别等 100-Continue：有些代理不回这个响应，会白等到超时
        client.DefaultRequestHeaders.ExpectContinue = false;
        return client;
    }

    // ─────────────────────────── 检查 ───────────────────────────

    /// <summary>
    /// 启动时的静默检查：超时 / 断网 / 500 / JSON 坏掉统统吞掉返回 null。
    /// ★ 这条路上绝对不许出现 MessageBox —— 用户没点任何东西，弹错误框就是干扰。
    /// </summary>
    public static async Task<RemoteRelease?> CheckSilentlyAsync()
    {
        // 主源 jsDelivr 拉不到（超时 / 断网 / CDN 抽风）就换 GitHub raw 再试一次；
        // 两路都失败也不抛——启动检查失败不该让用户看到任何东西。
        foreach (var url in Sources(DefaultFallbackUrl))
        {
            try
            {
                var remote = await FetchAsync(url);
                if (remote is null) continue;   // 这个源返回的不是合法清单，换下一个
                return IsNewer(remote, LocalVersion) ? remote : null;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }   // 超时走这里
            catch (Exception) { }               // JSON 坏掉之类
        }

        return null;
    }

    /// <summary>
    /// 手动检查：先 jsDelivr，失败或没内容再试 GitHub raw；两路都失败才把异常抛给「关于」窗口。
    /// </summary>
    public static async Task<RemoteRelease?> CheckManualAsync(string? fallbackUrl)
    {
        Exception? lastError = null;

        foreach (var url in Sources(fallbackUrl))
        {
            try
            {
                var remote = await FetchAsync(url);
                if (remote is not null) return remote;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null) throw lastError;
        return null;
    }

    /// <summary>主源 + 备用源（config.json 配了用配置的，没配用 GitHub raw），去重。</summary>
    static IEnumerable<string> Sources(string? fallbackUrl)
    {
        yield return VersionUrl;

        var fallback = string.IsNullOrWhiteSpace(fallbackUrl)
            ? DefaultFallbackUrl
            : AppConfig.ToRawUrl(fallbackUrl!.Trim());

        if (!string.Equals(fallback, VersionUrl, StringComparison.OrdinalIgnoreCase))
            yield return fallback;
    }

    static async Task<RemoteRelease?> FetchAsync(string url)
    {
        // 纯异步 GetStringAsync：不在同步上下文上阻塞，UI 线程等它也不会死锁。
        // 破缓存靠 URL 上的 ?t=（CDN 只认地址，不看请求头），刚发版时才不会读到旧的 version.json。
        var json = await Http.GetStringAsync(WithCacheBuster(url));
        return RemoteRelease.Parse(json, SourceName(url));
    }

    static string WithCacheBuster(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    static string SourceName(string url) =>
        url.Contains("jsdelivr.net", StringComparison.OrdinalIgnoreCase) ? "jsDelivr"
        : url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase) ? "Gitee"
        : url.Contains("github", StringComparison.OrdinalIgnoreCase) ? "GitHub"
        : "未知源";

    /// <summary>
    /// 本地版本。★ 不能用 <c>Assembly.GetName().Version</c>：程序集版本默认是 1.0.0.0，
    /// 拿它比版本会让每次启动都以为「有新版本」。真源是主程序 <c>Updates\VersionInfo.cs</c> 的常量。
    /// </summary>
    public static Version LocalVersion { get; } = ParseVersion(VersionInfo.Version);

    static Version ParseVersion(string text)
    {
        // "1.2.0-beta.1" → 取主.次.修三段
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
        {
            var build = parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : 0;
            return new Version(major, minor, build);
        }

        return new Version(0, 0, 0);
    }

    /// <summary>远端版本号能解析成数字且比本地大，才算「有新版本」。</summary>
    public static bool IsNewer(RemoteRelease? remote, Version local)
    {
        if (remote is null) return false;
        if (!Version.TryParse(Clean(remote.Version), out var remoteVersion)) return false;
        return remoteVersion > local;
    }

    static string Clean(string version)
    {
        var text = (version ?? "").Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => parts[0] + ".0",
            2 => parts[0] + "." + parts[1],
            >= 3 => parts[0] + "." + parts[1] + "." + parts[2],
            _ => "0.0",
        };
    }

    // ─────────────────────────── 拉起更新器 ───────────────────────────

    /// <summary>
    /// 成功返回 true（调用方应立刻退出本程序）；失败返回 false 并把原因写进 <paramref name="error"/>。
    /// 只复制 exe：单文件发布产物就是一个 IDEUpdater.exe；多文件 build 时把同名兄弟文件一起带上。
    /// </summary>
    public static bool TryStart(RemoteRelease remote, out string error)
    {
        error = "";

        if (remote.Sha256.Length != 64)
        {
            error = "远端 sha256 缺失或格式不对，为避免装上半个包，已拒绝更新。";
            return false;
        }

        if (!remote.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            error = "远端下载地址不合法：" + remote.DownloadUrl;
            return false;
        }

        var installDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var updaterPath = Path.Combine(installDir, UpdaterExeName);
        if (!File.Exists(updaterPath))
        {
            error = "本机没有更新器：" + updaterPath;
            return false;
        }

        var tempExe = Path.Combine(Path.GetTempPath(), TempUpdaterName);
        try
        {
            // 复制到 temp 再跑：就地运行会锁住自己，覆盖阶段必然失败
            File.Copy(updaterPath, tempExe, overwrite: true);

            var info = new ProcessStartInfo
            {
                FileName = tempExe,
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = true,
            };
            info.ArgumentList.Add("--url=" + remote.DownloadUrl);
            info.ArgumentList.Add("--hash=" + remote.Sha256);
            info.ArgumentList.Add("--target=" + installDir);
            info.ArgumentList.Add("--exe=" + MainExeName);

            if (Process.Start(info) is null)
            {
                error = "更新器没能启动。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "启动更新器失败：" + ex.Message;
            return false;
        }
    }
}

/// <summary>version.json 里 IDE 关心的那几个字段（schema 与主程序一致：stable / beta 两块）。</summary>
public sealed record RemoteRelease(string Version, string DownloadUrl, string Sha256, string Date, string Source)
{
    public static RemoteRelease? Parse(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 先看 stable，没有就看 beta；都没有就当没得更新
            if (!TryBlock(root, "stable", out var block) && !TryBlock(root, "beta", out block)) return null;

            var version = Text(block, "version");
            var url = Text(block, "download_url");
            if (version.Length == 0 || url.Length == 0) return null;

            return new RemoteRelease(version, url, Text(block, "sha256"), Text(block, "release_date"), source);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static bool TryBlock(JsonElement root, string key, out JsonElement block)
    {
        block = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty(key, out var found)) return false;
        if (found.ValueKind != JsonValueKind.Object) return false;

        block = found;
        return true;
    }

    static string Text(JsonElement block, string key) =>
        block.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}
