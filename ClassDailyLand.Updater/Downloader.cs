using System.Net.Http;
using System.Net.Http.Headers;

namespace ClassDailyLand.Updater;

public sealed record DownloadProgress(long Received, long Total, double BytesPerSecond);

/// <summary>
/// 流式下载（带进度 / 续传 / 双地址回退 / 停滞看门狗）。
///
/// 关键点：
///   · **流式**，不能 ReadAsByteArrayAsync —— 资源包可能几十 MB，一次性读进内存会爆；
///   · **节流上报**（150ms 或 512KB 一报），否则每 16KB 报一次会把 UI 刷爆；
///   · **续传**（<c>Range</c> + <c>.part</c>），断网重来时不必从头下；
///   · **停滞看门狗**：某些网络会"连着但不给数据"，只靠 HttpClient.Timeout 不够。
/// </summary>
public static class Downloader
{
    static readonly TimeSpan StagnationTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 下载到 <paramref name="destPath"/>（同目录 <c>.part</c> 落盘，完成后改名）。
    /// 返回下载好的文件路径；失败返回 null + 人话原因。
    /// </summary>
    public static async Task<(string? Path, string? Error)> FetchAsync(
        string url, string urlFallback, string destPath,
        long expectedSize, int timeoutSeconds, int maxAttempts,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var attempts = Math.Max(1, maxAttempts);
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
        if (!string.IsNullOrWhiteSpace(urlFallback) && !urls.Contains(urlFallback)) urls.Add(urlFallback);

        if (urls.Count == 0) return (null, "没有可用的下载地址");

        var lastError = "";
        for (var attempt = 0; attempt < attempts && !ct.IsCancellationRequested; attempt++)
        {
            foreach (var target in urls)
            {
                var (ok, error) = await FetchOnceAsync(target, destPath, expectedSize, timeoutSeconds, progress, ct)
                    .ConfigureAwait(false);

                if (ok) return (destPath, null);

                lastError = error;
                UpdaterLog.Warn($"下载失败（第 {attempt + 1} 次 / {UpdateSourcesName(target)}）：{error}");

                if (ct.IsCancellationRequested) return (null, "用户取消");
            }

            if (attempt + 1 < attempts) await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false);
        }

        return (null, string.IsNullOrWhiteSpace(lastError) ? "下载失败" : lastError);
    }

    static async Task<(bool Ok, string Error)> FetchOnceAsync(
        string url, string destPath, long expectedSize, int timeoutSeconds,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var partPath = destPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        var already = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds)) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ClassDailyLand-Updater/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (already > 0)
            request.Headers.Range = new RangeHeaderValue(already, null);

        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // 416 = Range 越界（.part 比远端还大），从头下
            if ((int)response.StatusCode == 416 && already > 0)
            {
                File.Delete(partPath);
                already = 0;
                return await FetchOnceAsync(url, destPath, expectedSize, timeoutSeconds, progress, ct)
                    .ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var total = expectedSize > 0
                ? expectedSize
                : already + (response.Content.Headers.ContentLength ?? 0);

            // 续传时服务器不支持 Range 会返回 200 全量 —— 这时要从头写
            var appending = already > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            var received = appending ? already : 0;

            if (!appending && File.Exists(partPath)) File.Delete(partPath);

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(partPath,
                appending ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                var lastReport = DateTime.UtcNow;
                var lastBytes = received;
                var lastAlive = DateTime.UtcNow;

                while (!ct.IsCancellationRequested)
                {
                    var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    lastAlive = DateTime.UtcNow;

                    // 停滞看门狗：一直没读到东西就是网络卡死
                    if (DateTime.UtcNow - lastAlive > StagnationTimeout)
                        return (false, "下载停滞（网络长时间没有数据）");

                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150) || received - lastBytes > 512 * 1024)
                    {
                        var speed = (received - lastBytes) /
                                    Math.Max(0.001, (DateTime.UtcNow - lastReport).TotalSeconds);
                        progress?.Report(new DownloadProgress(received, total, speed));
                        lastReport = DateTime.UtcNow;
                        lastBytes = received;
                    }
                }

                await target.FlushAsync(ct).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) return (false, "用户取消");

            progress?.Report(new DownloadProgress(received, total > 0 ? total : received, 0));

            // 大小对不上 = 没下完（除非远端没给长度）
            if (expectedSize > 0 && received != expectedSize)
            {
                File.Delete(partPath);
                return (false, $"下载不完整（{received} / {expectedSize} 字节）");
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(partPath, destPath);
            return (true, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "用户取消");
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.Message);
        }
        catch (IOException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name} {ex.Message}");
        }
    }

    static string UpdateSourcesName(string url)
    {
        if (url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase)) return "Gitee";
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase)) return "GitHub";
        return "下载源";
    }
}
