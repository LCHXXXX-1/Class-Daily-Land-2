using ClassDailyLand.UpdateContract;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClassDailyLand.App.Updates;

/// <summary>
/// 远端 <c>version.json</c> 的取数：双源（Gitee 主 / GitHub 备）+ 破缓存。
///
/// 为什么要破缓存：Gitee 的 <c>raw/</c> 走 CDN，发布后一段时间内还会吐旧文件，
/// 用户点了检查却看不到新版本，会以为功能坏了。加 <c>?t=&lt;unix&gt;</c> + <c>no-cache</c>
/// 把这次请求当新的取，不给 CDN 用缓存的机会。
/// </summary>
public static class UpdateSources
{
    const string UserAgent = "ClassDailyLand-Updater/1.0";

    /// <summary>单次请求的超时（秒）。不足 10 秒时，国内网络抖一下就会被误判成"更新源挂了"。</summary>
    const int TimeoutSeconds = 12;

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

    static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// ★ 超时交给每次请求自己的 linked CTS，<see cref="HttpClient.Timeout"/> 设成无限。
    ///   .NET 5+ 里 HttpClient.Timeout 到期抛的是 <see cref="TaskCanceledException"/>，
    ///   而且 InnerException 还套一层 TimeoutException —— 调试器的"第一现场"要停两次。
    ///   用自己的 CTS 到期只会得到一条干净的取消异常。
    /// </summary>
    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,                 // 走系统代理（校园网 / 公司网常见）
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain;q=0.9");
        return client;
    }

    /// <summary>
    /// 逐个源尝试拉 version.json，返回第一个成功解析的。
    /// 全部失败时 <c>error</c> 给出**每个源各自的失败原因**（排障时用户只知道"Gitee 挂了还是 GitHub 挂了"）。
    /// </summary>
    public static async Task<(RemoteVersionFile? File, string Source, string Error)> FetchAsync(
        IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        if (urls is null || urls.Count == 0)
            return (null, "", "没有配置更新源（version_url 为空）");

        var errors = new List<string>();

        foreach (var raw in urls)
        {
            var url = WithCacheBuster(raw);
            var name = SourceName(raw);

            // 每个源各自计时：一个源慢不应该吃掉另一个源的预算
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Timeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, must-revalidate");
                request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

                using var response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{name}：HTTP {(int)response.StatusCode}");
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
                var text = DecodeBody(bytes);

                if (string.IsNullOrWhiteSpace(text))
                {
                    errors.Add($"{name}：空响应");
                    continue;
                }

                RemoteVersionFile? file;
                try
                {
                    file = JsonSerializer.Deserialize<RemoteVersionFile>(text, UpdateJson.Options);
                }
                catch (JsonException ex)
                {
                    errors.Add($"{name}：内容不是合法 JSON（{ex.Message}）");
                    continue;
                }

                if (file is null)
                {
                    errors.Add($"{name}：解析结果为空");
                    continue;
                }

                return (file, name, "");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // 自己的 deadline 到期（不是用户取消）：按超时记账，继续试下一个源
                errors.Add($"{name}：请求超时（{TimeoutSeconds} 秒）");
            }
            catch (OperationCanceledException)
            {
                // 用户/上层取消：静默返回，不弹窗、不当成"更新源故障"
                if (ct.IsCancellationRequested) return (null, name, "用户取消");
                errors.Add($"{name}：请求已取消");
            }
            catch (HttpRequestException ex)
            {
                errors.Add($"{name}：{ex.Message}");
            }
            catch (Exception ex)
            {
                errors.Add($"{name}：{ex.GetType().Name} {ex.Message}");
            }
        }

        return (null, "", string.Join("；", errors));
    }

    /// <summary>主源失败时用这个拿另一个源的地址，供「立即更新」时补下载兜底地址。</summary>
    public static async Task<(string? DownloadUrl, string Source)> TryGetDownloadUrlAsync(
        string versionJsonUrl, string channel, CancellationToken ct = default)
    {
        var (file, source, _) = await FetchAsync(new[] { versionJsonUrl }, ct).ConfigureAwait(false);
        if (file is null) return (null, source);

        var block = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? (RemoteChannel?)file.Beta
            : file.Stable;

        return (block is null || string.IsNullOrWhiteSpace(block.DownloadUrl) ? null : block.DownloadUrl.Trim(), source);
    }

    /// <summary>URL 加 <c>?t=&lt;unix&gt;</c>，让 CDN 当成全新请求。</summary>
    static string WithCacheBuster(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    /// <summary>从 URL 猜一个源名字，用于提示语。</summary>
    public static string SourceName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "未知源";
        if (url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase)) return "Gitee";
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase)) return "GitHub";
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return "未知源";
        }
    }

    /// <summary>UTF-8 → GB18030 兜底（与灾害预警同款：手改的文件可能带 BOM 或 GBK）。</summary>
    static string DecodeBody(byte[] bytes)
    {
        if (bytes.Length == 0) return "";

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding("GB18030").GetString(bytes); }
            catch { return Encoding.UTF8.GetString(bytes); }
        }
    }
}
