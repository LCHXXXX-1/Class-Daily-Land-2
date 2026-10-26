using System.Net.Http;
using ClassDailyLand2.UpdateContract;

namespace ClassDailyLand2.Updater;

/// <summary>下载进度（字节）。界面拿它自己折算成百分比 —— 更新器不猜界面想要什么刻度。</summary>
public readonly record struct DownloadProgressReport(long Received, long Total);

/// <summary>
/// 下载安装包。
///
/// 契约里的 <c>timeout_seconds</c> 是「**多久没收到任何字节**」而不是「总耗时」——
/// 几十 MB 的包在慢网络上本来就要跑几分钟，用总超时会把正常下载掐死；
/// 反过来网络卡住时又要能尽快换源重试。所以按「每次读」重置超时。
/// </summary>
internal static class Downloader
{
    private const int BufferSize = 64 * 1024;
    private const long LogEveryBytes = 8L * 1024 * 1024;

    public static async Task<(string? Path, int Code, string? Error)> DownloadAsync(
        JobDownload download, string zipPath, JobLog log,
        IProgress<DownloadProgressReport>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(download.Url))
            return (null, UpdateExitCode.BadJob, "任务里没有下载地址");

        var urls = BuildUrlList(download);
        var attempts = Math.Max(1, download.MaxAttempts);
        string? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    log.Write($"下载（第 {attempt}/{attempts} 次）：{url}");
                    progress?.Report(new DownloadProgressReport(0, download.Size));

                    var bytes = await DownloadOneAsync(url, zipPath, download, log, progress, ct)
                        .ConfigureAwait(false);

                    log.Write($"下载完成：{bytes} 字节");
                    return (zipPath, UpdateExitCode.Success, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 用户取消：一路抛给界面，不要记成"下载失败"
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                              or OperationCanceledException or InvalidOperationException)
                {
                    lastError = $"{url} → {ex.Message}";
                    log.Write("下载失败：" + lastError);
                    TryDelete(zipPath);
                }
            }

            if (attempt < attempts)
            {
                var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                log.Write($"{backoff.TotalSeconds:0} 秒后重试");
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
        }

        return (null, UpdateExitCode.DownloadFailed, "下载失败：" + lastError);
    }

    private static List<string> BuildUrlList(JobDownload download)
    {
        var urls = new List<string>();
        foreach (var candidate in new[] { download.Url, download.UrlFallback })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var url = candidate.Trim();
            if (urls.Any(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase))) continue;
            urls.Add(url);
        }
        return urls;
    }

    private static async Task<long> DownloadOneAsync(string url, string zipPath, JobDownload download,
                                                     JobLog log, IProgress<DownloadProgressReport>? progress,
                                                     CancellationToken ct)
    {
        // 走系统代理（校园网常见）；Timeout 设为无限，超时由下面逐次读的 CTS 负责
        var handler = new System.Net.Http.HttpClientHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClassDailyLand2-Updater/1.0");

        // 等响应头也计时（DNS 挂了 / 源站不响应时会卡很久）
        using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, download.TimeoutSeconds)));

        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headCts.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var expected = download.Size > 0
            ? download.Size
            : response.Content.Headers.ContentLength ?? 0;

        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dest = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, useAsync: true);

        var readTimeout = TimeSpan.FromSeconds(Math.Max(10, download.TimeoutSeconds));
        var buffer = new byte[BufferSize];
        long total = 0;
        long nextLog = LogEveryBytes;

        progress?.Report(new DownloadProgressReport(0, expected));

        while (true)
        {
            int read;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readCts.CancelAfter(readTimeout);
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"连续 {readTimeout.TotalSeconds:0} 秒没有收到任何数据（网络卡住）");
                }
            }

            if (read <= 0) break;

            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;

            progress?.Report(new DownloadProgressReport(total, expected));

            if (total >= nextLog)
            {
                nextLog += LogEveryBytes;
                log.Write(expected > 0
                    ? $"已下载 {total / 1048576} MB / {expected / 1048576} MB"
                    : $"已下载 {total / 1048576} MB");
            }
        }

        await dest.FlushAsync(ct).ConfigureAwait(false);

        if (expected > 0 && total != expected)
            throw new IOException($"下载字节数与预期不符：预期 {expected}，实际 {total}");

        if (total <= 0)
            throw new IOException("下载到的是空文件");

        return total;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 清不掉的半截文件下次会被覆盖 */ }
    }
}
