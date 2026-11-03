using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassDailyLand2.Core;

namespace DeveloperTool.Updates;

/// <summary>
/// 更新链路：拉 version.json、比版本、拉起更新器。
/// 网络层对齐主程序 UpdateSources，解析严格只认 developer_tool 节点。
/// </summary>
public static class UpdateLauncher
{
    const string UserAgent = "ClassDailyLand2-DeveloperTool/1.0";
    const int TimeoutSeconds = 12;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true, // 让系统自己处理，不手动 GetSystemWebProxy（避开 PAC 卡死）
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };

        // Timeout 交给每次请求的 linked CTS，防止 Gitee 慢时直接抛 TaskCanceledException
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain;q=0.9");
        return client;
    }

    // ─────────────────────────── 检查 ───────────────────────────

    /// <summary>启动时的静默检查，任何异常都吞掉返回 null。</summary>
    public static async Task<RemoteRelease?> CheckSilentlyAsync()
    {
        try
        {
            var remote = await FetchAsync(VersionInfo.VersionUrl);
            if (remote is null) return null;
            return IsNewer(remote, LocalVersion) ? remote : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>手动检查：主源失败换备用源，全失败抛异常给 UI 弹窗。</summary>
    public static async Task<RemoteRelease?> CheckManualAsync(string? fallbackUrl)
    {
        Exception? lastError = null;
        var urls = new List<string> { VersionInfo.VersionUrl };

        var fallback = string.IsNullOrWhiteSpace(fallbackUrl)
            ? VersionInfo.VersionUrlFallback
            : fallbackUrl.Trim();

        if (!string.IsNullOrWhiteSpace(fallback) && !string.Equals(fallback, VersionInfo.VersionUrl, StringComparison.OrdinalIgnoreCase))
            urls.Add(fallback);

        foreach (var url in urls)
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

    static async Task<RemoteRelease?> FetchAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var separator = url.Contains('?') ? '&' : '?';
        var busted = $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        using var deadline = new CancellationTokenSource(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, busted);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, must-revalidate");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
        var json = DecodeBody(bytes);

        return RemoteRelease.Parse(json, SourceName(url));
    }

    static string DecodeBody(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch { return Encoding.UTF8.GetString(bytes); }
    }

    static string SourceName(string url) =>
        url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase) ? "Gitee"
        : url.Contains("github", StringComparison.OrdinalIgnoreCase) ? "GitHub"
        : "CDN";

    public static Version LocalVersion { get; } = ParseVersion(VersionInfo.Version);

    static Version ParseVersion(string text)
    {
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];

        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
        {
            var build = parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : 0;
            return new Version(major, minor, build);
        }
        return new Version(0, 0, 0);
    }

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
    /// 更新器是框架依赖，必须把整个 Update\ 目录搬到 %Temp% 再启动，
    /// 否则会锁住文件导致替换失败。
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

        var sourceDir = Path.Combine(installDir, "Update");
        var sourceExe = Path.Combine(sourceDir, "IDEUpdater.exe");

        if (!File.Exists(sourceExe))
        {
            error = "本机没有更新器：" + sourceExe;
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "DeveloperTool.Update");

        try
        {
            // 1. 清空旧临时目录
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            Directory.CreateDirectory(tempDir);

            // 2. 整目录复制
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var dest = Path.Combine(tempDir, relative);

                var parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                File.Copy(file, dest, overwrite: true);
            }

            var tempExe = Path.Combine(tempDir, "IDEUpdater.exe");

            // 3. 拉起更新器
            // ★ 必须 UseShellExecute = false，否则 ArgumentList 会被系统忽略（参数丢失）
            var info = new ProcessStartInfo
            {
                FileName = tempExe,
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // ★ ArgumentList 分开添加，不能写 "--url=xxx" 这种等号拼接
            info.ArgumentList.Add("--url");
            info.ArgumentList.Add(remote.DownloadUrl);
            info.ArgumentList.Add("--hash");
            info.ArgumentList.Add(remote.Sha256);
            info.ArgumentList.Add("--target");
            info.ArgumentList.Add(installDir);
            info.ArgumentList.Add("--exe");
            info.ArgumentList.Add("DeveloperTool.exe");

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

/// <summary>解析 version.json 里的 developer_tool 节点。</summary>
public sealed record RemoteRelease(string Version, string DownloadUrl, string Sha256, string Date, string Source)
{
    public static RemoteRelease? Parse(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // ★ 严格只读 developer_tool 节点，绝不回退到 stable！
            if (!TryBlock(root, "developer_tool", out var block)) return null;

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