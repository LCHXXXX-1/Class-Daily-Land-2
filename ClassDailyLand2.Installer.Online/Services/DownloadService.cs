using System.Net;
using System.Net.Http;

namespace ClassDailyLand2.Installer.Online.Services;

/// <summary>
/// 进度上报：百分比 + 给人看的一句话（百分比是全局进度，不是单个文件），
/// 外加已下载/总字节数 —— 界面靠它算实时速度。
/// </summary>
internal readonly record struct DownloadProgress(double Percent, string? Message,
                                                 long Received = 0, long Total = 0);

/// <summary>安装流程里"预期内"的失败：消息直接显示给用户，不当作崩溃。</summary>
internal sealed class InstallFailedException : Exception
{
    public InstallFailedException(string message) : base(message) { }
    public InstallFailedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 下载（主程序整包 / 运行库安装包）。
///
/// 超时口径：<c>15 秒</c> 管两件事 —— 等响应头、以及"多久没收到下一块数据"；
/// 它**不是**总耗时。几十 MB 的包在慢网络上本来就要跑几分钟，用总超时会把正常下载掐死。
/// </summary>
internal static class DownloadService
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/125.0.0.0 Safari/537.36 ClassDailyLand2-Setup/1.0";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task DownloadAsync(IReadOnlyList<string> urls, string destination,
                                           IProgress<DownloadProgress>? progress,
                                           double fromPercent, double toPercent, CancellationToken ct)
    {
        if (urls.Count == 0) throw new InstallFailedException("没有可用的下载地址。");

        var errors = new List<string>();

        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(new DownloadProgress(fromPercent, "连接：" + Shorten(url)));
                await DownloadOneAsync(url, destination, progress, fromPercent, toPercent, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{Shorten(url)} → {ex.Message}");
            }
        }

        throw new InstallFailedException("所有下载地址都失败了：" + string.Join("；", errors));
    }

    private static async Task DownloadOneAsync(string url, string destination,
                                               IProgress<DownloadProgress>? progress,
                                               double fromPercent, double toPercent, CancellationToken ct)
    {
        using var client = CreateClient();

        // 等响应头也限 15 秒（DNS 失败、源站挂掉时会卡很久）
        using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headCts.CancelAfter(Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headCts.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        var part = destination + ".part";

        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var buffer = new byte[81920];
        long received = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                                               buffer.Length, useAsync: true))
        {
            while (true)
            {
                int read;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(Timeout);
                    try
                    {
                        read = await source.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"连续 {Timeout.TotalSeconds:0} 秒没有收到数据");
                    }
                }

                if (read <= 0) break;

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (total > 0)
                {
                    var percent = fromPercent + (toPercent - fromPercent) * ((double)received / total);
                    progress?.Report(new DownloadProgress(percent,
                        $"下载中 {received / 1048576.0:0.0} / {total / 1048576.0:0.0} MB",
                        received, total));
                }
                else if (received % (4 * 1024 * 1024) < buffer.Length)
                {
                    progress?.Report(new DownloadProgress(fromPercent,
                        $"下载中 {received / 1048576.0:0.0} MB", received, 0));
                }
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
        }

        if (total > 0 && received != total)
            throw new IOException($"下载不完整：{received} / {total} 字节");

        if (received == 0)
            throw new IOException("下载到的是空文件");

        File.Move(part, destination, overwrite: true);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,                                  // 走系统代理（校园网常见）
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private static string Shorten(string url) =>
        url.Length <= 64 ? url : url[..40] + "…" + url[^18..];
}
