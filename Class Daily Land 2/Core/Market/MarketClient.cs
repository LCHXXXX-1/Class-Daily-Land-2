using System.Net;
using System.Net.Http;
using System.Text;

namespace ClassDailyLand2.Core.Market;

/// <summary>一次拉取的结果。<see cref="Index"/> 为 null 表示这份源没拿到可用的索引。</summary>
public sealed record MarketFetch(
    MarketIndex? Index,
    string Source,
    string Error,
    IReadOnlyList<string> Warnings,
    DateTime FetchedAt)
{
    public bool Ok => Index is not null;
}

/// <summary>
/// 索引的取数：双源（Gitee 主 / GitHub 备）+ 破缓存 + 每个源单独计时。
///
/// <para>与 <c>UpdateSources</c> 同一套思路，但有两处是插件市场特有的：</para>
/// <list type="bullet">
///   <item>Gitee 的 <c>/raw/</c> 现在会 302 到签名地址 —— 必须允许重定向；</item>
///   <item>索引里的 <c>path</c> 是相对路径，安装包 URL 由 <see cref="PackageUrls"/> 现拼，
///         主备源共用同一条记录，下载失败还能换源重试。</item>
/// </list>
/// </summary>
public static class MarketClient
{
    const string UserAgent = "ClassDailyLand2-Market/1.0";

    /// <summary>单个源超时（秒）。低到 10 秒以内，国内网络抖一下就会被误判成源挂了。</summary>
    const int TimeoutSeconds = 15;

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>索引与安装包共用同一个客户端（代理、重定向、解压都按同一套配）。</summary>
    internal static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,                 // 校园网 / 公司网常见
            AllowAutoRedirect = true,        // ★ Gitee raw 会 302 到 raw.giteeusercontent.com
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 5,
        };

        // 超时交给每次请求自己的 CTS（理由见 UpdateSources.CreateClient 的注释）
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain;q=0.9");
        return client;
    }

    /// <summary>逐个源尝试，返回第一个解析成功的索引；全失败时给出每个源各自的原因。</summary>
    public static async Task<MarketFetch> FetchAsync(IReadOnlyList<string> urls, CancellationToken ct = default)
    {
        if (urls is null || urls.Count == 0)
            return new MarketFetch(null, "", "没有配置市场源", Array.Empty<string>(), DateTime.Now);

        var errors = new List<string>();

        foreach (var raw in urls)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var name = SourceName(raw);

            // 每个源最多试两次：进程内的第一次 TLS 握手（走系统代理时）偶尔会顶到超时，
            // 第二次通常 1 秒内就回来了。少这一次重试，备用源就等于不存在。
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var url = WithCacheBuster(raw);
                Logger.Current?.Trace($"[市场] 拉索引（第 {attempt}/2 次，{name}）：{url}");

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
                        break;
                    }

                    // ★ 索引响应也要有上限（安装包下载有 64MB 校验，索引原来没有）：
                    //   索引正常只有几十 KB，1MB 已经很宽 —— 被劫持的源塞一个几百 MB
                    //   的 index.json 就能把内存撑爆。
                    const long maxBytes = 1024 * 1024;
                    if (response.Content.Headers.ContentLength is > maxBytes)
                    {
                        errors.Add($"{name}：响应过大");
                        break;
                    }

                    byte[] bytes;
                    await using (var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        var chunk = new byte[8192];
                        long total = 0;
                        var tooLarge = false;
                        while (true)
                        {
                            var read = await body.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
                            if (read <= 0) break;
                            total += read;
                            if (total > maxBytes) { tooLarge = true; break; }
                            buffer.Write(chunk, 0, read);
                        }
                        if (tooLarge)
                        {
                            errors.Add($"{name}：响应超过 1MB 上限");
                            break;
                        }
                        bytes = buffer.ToArray();
                    }

                    var text = DecodeBody(bytes);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        errors.Add($"{name}：空响应");
                        break;
                    }

                    var parsed = MarketIndex.Parse(text);
                    if (!parsed.Ok)
                    {
                        errors.Add($"{name}：{parsed.Error}");
                        break;
                    }

                    return new MarketFetch(parsed.Index, name, "", parsed.Warnings, DateTime.Now);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt == 1) continue;
                    errors.Add($"{name}：请求超时（{TimeoutSeconds} 秒）");
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) return new MarketFetch(null, name, "用户取消", Array.Empty<string>(), DateTime.Now);
                    errors.Add($"{name}：请求已取消");
                    break;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt == 1) continue;
                    errors.Add($"{name}：{ex.Message}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}：{ex.GetType().Name} {ex.Message}");
                    break;
                }
            }
        }

        return new MarketFetch(null, "", string.Join("；", errors), Array.Empty<string>(), DateTime.Now);
    }

    /// <summary>
    /// 一个条目的候选下载地址：绝对 URL 直接用（Releases 逃生口），
    /// 相对路径按「主源根 → 备源根」拼两份，前者失败再试后者。
    /// </summary>
    public static IReadOnlyList<string> PackageUrls(MarketEntry entry, IReadOnlyList<string> rawRoots)
    {
        if (string.IsNullOrWhiteSpace(entry.Path)) return Array.Empty<string>();
        if (entry.HasAbsolutePackage) return new[] { entry.Path };

        var relative = entry.Path.Replace('\\', '/').TrimStart('/');
        var urls = new List<string>(rawRoots.Count);
        foreach (var root in rawRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var url = root.TrimEnd('/') + "/" + relative;
            if (!urls.Contains(url, StringComparer.OrdinalIgnoreCase)) urls.Add(url);
        }
        return urls;
    }

    /// <summary>URL 加 <c>?t=&lt;unix&gt;</c>：Gitee 与 GitHub 的 raw 都吃 CDN 缓存。</summary>
    public static string WithCacheBuster(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    /// <summary>从 URL 猜源名（提示语与日志用）。</summary>
    public static string SourceName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "未知源";
        if (url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase)) return "Gitee";
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase)) return "GitHub";
        if (url.Contains("githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return "GitHub";
        try { return new Uri(url).Host; } catch { return "未知源"; }
    }

    /// <summary>UTF-8 → GB18030 兜底（手改过的索引可能带 BOM 或 GBK）。</summary>
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
