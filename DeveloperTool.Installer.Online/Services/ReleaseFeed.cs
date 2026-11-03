using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DeveloperTool.Installer.Online.Services;

/// <summary>从远端 version.json 的 <c>developer_tool</c> 节点里取出来的发布信息。</summary>
internal sealed record ReleaseInfo(string Version, string DownloadUrl, string Sha256);

/// <summary>
/// 拉取远端 <c>version.json</c> 并取出 IDE 专属的 <c>developer_tool</c> 节点。
///
/// 这个文件和主程序共用，多通道结构里 IDE 的包单独挂一个节点：
/// <code>
/// {
///   "stable":         { "version": "1.2", "download_url": "…", "sha256": "…" },
///   "developer_tool": { "version": "0.9", "download_url": "…", "sha256": "…" }
/// }
/// </code>
///
/// ★ 只认 <c>developer_tool</c>，**绝不**回退读 stable 或根节点 ——
///   回退一次就会把主程序整包装成 IDE，这比"读不到就报错"严重得多。
/// </summary>
internal static class ReleaseFeed
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/125.0.0.0 Safari/537.36 ClassDailyLand2-Setup/1.0";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 拉取 Gitee 上的 version.json，取出 <c>developer_tool</c> 节点。
    ///
    /// 单一源：连不上就把原因收成一句人话抛给界面（红字 + 重试），
    /// 不做静默换源、也不做后台重试 —— 备源只会把"到底谁坏了"变成一团谜。
    /// </summary>
    public static async Task<ReleaseInfo> FetchAsync(CancellationToken ct)
    {
        string json;

        try
        {
            json = await GetStringAsync(SetupEndpoints.VersionUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户点了取消
        }
        catch (Exception ex)
        {
            // 超时 / 断网 / 403 / 302 跟丢了：统一成同一句话
            throw new InstallFailedException(SetupEndpoints.NetworkErrorMessage, ex);
        }

        ReleaseInfo? info;

        try
        {
            info = Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InstallFailedException("更新服务器返回的内容不是合法 JSON（version.json 可能被改坏了）", ex);
        }

        return info ?? throw new InstallFailedException(
            "更新服务器上的 version.json 缺少 developer_tool 节点或 download_url");
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true,              // Gitee raw 会 302 跳到 CDN
            AutomaticDecompression = DecompressionMethods.All,
        };

        using var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain;q=0.9");

        // 破缓存：Gitee raw 走 CDN，刚发版时可能还吐旧文件
        var separator = url.Contains('?') ? '&' : '?';
        var busted = $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, busted);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, must-revalidate");

        using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 只读 root 下的 <c>developer_tool</c> 节点；没有、或不是对象，一律返回 null。
    /// 刻意不提供任何回退分支 —— 见类注释。
    /// </summary>
    private static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("developer_tool", out var node) && node.ValueKind == JsonValueKind.Object)
        {
            var fromChannel = Read(node);
            if (fromChannel is not null) return fromChannel;
        }

        return null;
    }

    private static ReleaseInfo? Read(JsonElement element)
    {
        var version = Str(element, "version");
        var url = Str(element, "download_url");

        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) return null;

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ReleaseInfo(version.Trim(), url.Trim(), Str(element, "sha256")?.Trim() ?? "");
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
