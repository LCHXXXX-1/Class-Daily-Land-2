using Microsoft.Win32;
using System.Net;
using System.Net.Http;

namespace ClassDailyLand2.App.DeveloperTool;

/// <summary>
/// <see cref="AppController.OpenPluginDeveloperTool"/> 的三态结果。
/// 原来是 bool：界面分不清「没装」与「启动失败」，前者要引导下载，后者只需报错。
/// </summary>
public enum DeveloperToolOpenResult { Launched, NotInstalled, Failed }

/// <summary>在线安装器下载进度：百分比（0~100，总长未知时恒为 0）+ 已下/总字节 + 可选一句话。</summary>
public readonly record struct SetupDownloadProgress(double Percent, long Received, long Total, string? Message);

/// <summary>
/// 插件开发工具（DeveloperTool）的定位与安装引导。
///
/// DeveloperTool 已从主程序分离：主程序不再内置它，也不再随更新包发布，
/// 改由在线安装器（DeveloperTool_Setup_Online.exe）单独安装。
/// 这里只负责两件事：找到装好的它；没装时把安装器下载下来交给界面去拉起。
/// </summary>
public static class DeveloperToolService
{
    /// <summary>在线安装器下载地址（Gitee release 的 install 资产）。发版换文件只改这一处。</summary>
    public const string SetupUrl =
        "https://gitee.com/lchxxxx/class-daily-land-2/releases/download/install/DeveloperTool_Setup_Online.exe";

    /// <summary>下载到临时目录后的文件名（与安装器自身产物同名，用户一眼认出来）。</summary>
    public const string SetupFileName = "DeveloperTool_Setup_Online.exe";

    private const string ExeFileName = "DeveloperTool.exe";

    /// <summary>DeveloperTool 在线安装器写的卸载条目（HKCU，按当前用户算，与它的安装位置一致）。</summary>
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeveloperTool";

    /// <summary>
    /// 查找已安装的 DeveloperTool.exe，三个位置依次来，任意命中即返回完整路径：
    ///   1. 卸载注册表的 InstallLocation（在线安装器写，最权威）；
    ///   2. 默认安装位置 <c>%LocalAppData%\DeveloperTool</c>（注册表被清理过也能命中）；
    ///   3. 旧版随主程序部署的「主程序目录\DeveloperTool」——分离之前装的用户
    ///      不重装也能继续用老的一份（红线：不能让老用户点「前往」就报错）。
    /// </summary>
    public static string? FindInstalledExe()
    {
        foreach (var dir in CandidateDirectories())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            var exe = Path.Combine(dir, ExeFileName);
            try
            {
                if (File.Exists(exe)) return exe;
            }
            catch
            {
                // 路径本身炸了（映射盘断开之类）：当这一处没命中，继续找下一处
            }
        }

        return null;
    }

    private static IEnumerable<string?> CandidateDirectories()
    {
        yield return ReadInstallLocation();
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeveloperTool");
        yield return Path.Combine(AppContext.BaseDirectory, "DeveloperTool");
    }

    private static string? ReadInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            // 读不到注册表不是失败，退回默认位置去找
            return null;
        }
    }

    /// <summary>
    /// 把在线安装器下载到 <c>%TEMP%\DeveloperTool_Setup_Online.exe</c> 并返回该路径。
    ///
    /// 口径与在线安装器自己的 DownloadService 一致：15 秒管「等响应头」和「多久没收到下一块数据」，
    /// 不是总耗时；Gitee release 会 302 跳到 CDN，必须 AllowAutoRedirect；
    /// 先落 .part 再改名，取消 / 失败都清掉半截文件，绝不留下一个「看起来完整」的 exe。
    /// </summary>
    public static async Task<string> DownloadSetupAsync(
        IProgress<SetupDownloadProgress>? progress, CancellationToken ct)
    {
        var destination = Path.Combine(Path.GetTempPath(), SetupFileName);
        var part = destination + ".part";

        try
        {
            using var client = CreateClient();

            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headCts.CancelAfter(IdleTimeout);

            using var response = await client
                .GetAsync(SetupUrl, HttpCompletionOption.ResponseHeadersRead, headCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new IOException($"下载服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}");

            var total = response.Content.Headers.ContentLength ?? 0;
            var buffer = new byte[81920];
            long received = 0;

            progress?.Report(new SetupDownloadProgress(0, 0, total, "正在下载在线安装器…"));

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write,
                                                       FileShare.None, buffer.Length, useAsync: true))
            {
                while (true)
                {
                    int read;
                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        readCts.CancelAfter(IdleTimeout);
                        try
                        {
                            read = await source.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new IOException($"连续 {IdleTimeout.TotalSeconds:0} 秒没有收到数据");
                        }
                    }

                    if (read <= 0) break;

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;

                    var percent = total > 0 ? (double)received / total * 100.0 : 0;
                    progress?.Report(new SetupDownloadProgress(percent, received, total, null));
                }

                await file.FlushAsync(ct).ConfigureAwait(false);
            }

            if (received == 0)
                throw new IOException("下载到的是空文件");

            if (total > 0 && received != total)
                throw new IOException($"下载不完整：{received} / {total} 字节");

            File.Move(part, destination, overwrite: true);
            return destination;
        }
        catch
        {
            TryDeleteSilently(part);
            throw;
        }
    }

    /// <summary>超时口径交给每次请求自己的 linked CTS；HttpClient.Timeout 设成无限（与在线安装器一致）。</summary>
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,                                  // 走系统代理（校园网常见）
            AllowAutoRedirect = true,                         // Gitee release 302 跳到 CDN
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/125.0.0.0 Safari/537.36 ClassDailyLand2/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

    private static void TryDeleteSilently(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 半截文件删不掉不影响报错 */ }
    }
}
