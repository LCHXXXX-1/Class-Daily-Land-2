using System.Net.Http;
using ClassDailyLand2.UpdateContract;

namespace CDL2.Updater;

/// <summary>下载进度（字节）。</summary>
public readonly record struct DownloadProgress(long Received, long? Total);

/// <summary>
/// 下载安装包。支持 http(s) 与 file://（本地路径，供沙箱测试与离线救援）。
///
/// 契约里的 timeout_seconds 是「**多久没收到任何字节**」而不是总耗时 ——
/// 大包慢网本来就要跑几分钟，按总时长掐会掐死正常下载。
/// 另有停滞检测：一个短窗口内字节数完全没涨（限速/卡流）就断开换源，而不是让用户干等。
/// </summary>
public static class Downloader
{
    private const int BufferSize = 64 * 1024;
    private const long LogEveryBytes = 8L * 1024 * 1024;

    public static async Task<(string? Path, int Code, string? Error)> DownloadAsync(
        JobDownload download, string zipPath, UpdaterLog log,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
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
                    progress?.Report(new DownloadProgress(0, download.Size));

                    long bytes;
                    if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("file:\\\\", StringComparison.OrdinalIgnoreCase) ||
                        (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                         !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                         File.Exists(url)))
                    {
                        bytes = await CopyLocalAsync(LocalPathFrom(url), zipPath, progress, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        bytes = await DownloadHttpAsync(url, zipPath, download, log, progress, ct).ConfigureAwait(false);
                    }

                    log.Write($"下载完成：{bytes} 字节");
                    return (zipPath, UpdateExitCode.Success, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                              or OperationCanceledException or InvalidOperationException
                                              or UnauthorizedAccessException or NotSupportedException)
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

    private static string LocalPathFrom(string url)
    {
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            return uri.LocalPath;
        }
        return url;
    }

    /// <summary>本地复制（file:// / 本地路径）：下载逻辑的测试替身，也当离线救援用。</summary>
    private static async Task<long> CopyLocalAsync(string sourcePath, string zipPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("本地包不存在", sourcePath);

        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var info = new FileInfo(sourcePath);
        progress?.Report(new DownloadProgress(0, info.Length));

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: true);
        await using var dest = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;

            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            progress?.Report(new DownloadProgress(total, info.Length));
        }

        await dest.FlushAsync(ct).ConfigureAwait(false);
        return total;
    }

    private static async Task<long> DownloadHttpAsync(string url, string zipPath, JobDownload download,
        UpdaterLog log, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // 走系统代理（校园网常见）；HttpClient.Timeout 设为无限，超时由逐次读的 CTS 负责
        using var handler = new HttpClientHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClassDailyLand2-Updater/2.0");

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

        // 停滞检测：timeout_seconds 是"零字节超时"，太钝——慢速爬行（每分钟几 KB）永远不会
        // 触发它，用户看到的就是"下不动"。加一道更灵敏的闸：短窗口内字节数没涨就断开换源。
        var stallWindow = TimeSpan.FromSeconds(Math.Clamp(download.TimeoutSeconds / 4, 10, 30));
        var lastProgress = (Bytes: 0L, At: DateTime.UtcNow);

        progress?.Report(new DownloadProgress(0, expected));

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
            progress?.Report(new DownloadProgress(total, expected));

            if (total > lastProgress.Bytes)
                lastProgress = (total, DateTime.UtcNow);
            else if (DateTime.UtcNow - lastProgress.At > stallWindow)
                throw new IOException($"超过 {stallWindow.TotalSeconds:0} 秒下载没有任何进展（疑似限速或卡流），换个源重试");

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
        catch { /* 半截文件下次会被覆盖 */ }
    }
}
