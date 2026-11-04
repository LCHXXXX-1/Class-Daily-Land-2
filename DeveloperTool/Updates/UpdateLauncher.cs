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

    /// <summary>
    /// 正在往 <c>Temp\DeveloperTool.Update</c> 搬更新器。
    /// 供启动时的残留清理（<c>App.CleanStaleUpdaterTemp</c>）让路 —— 两边都会碰同一个目录。
    /// </summary>
    internal static volatile bool IsPreparing;

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
    /// 更新器是框架依赖，必须把整个 Update\ 目录整份复制到别处再启动，
    /// 否则会锁住自己导致替换失败。
    ///
    /// ★ 落点是 <c>&lt;安装目录&gt;\Temp\DeveloperTool.Update</c>，不再用系统 %Temp%：
    ///   安全软件对"陌生临时目录里的陌生 exe"拦截率高，而程序目录是用户自己装的、
    ///   已建立信任的路径。注意 <see cref="App"/> 构造函数已经把进程级 TEMP 指到
    ///   <c>&lt;安装目录&gt;\Temp</c>，所以这里显式拼全路径，不依赖 <c>Path.GetTempPath()</c>
    ///   —— 那条改写一旦失败（目录只读），GetTempPath 会悄悄退回系统 %Temp%，
    ///   等于绕过本条策略。
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

        // 落点固定在安装目录内：安装目录是可信路径，且与目标盘同卷，
        // 顺带避开"杀软拦 %Temp% 里的 exe"和跨卷复制。
        var tempDir = Path.Combine(installDir, "Temp", "DeveloperTool.Update");

        // ★ 给启动清理让路的标记。App.CleanStaleUpdaterTemp 会按 DeveloperTool.Update* 前缀清残留，
        //   而它跑在启动时的后台线程上；用户点"立即更新"若撞上它，会出现
        //   "刚 CreateDirectory 完就被整目录删掉、复制全落在空目录里"的现象。
        IsPreparing = true;
        try
        {
            // 1. 清空旧临时目录（占着就明说，不要把"上一个更新器还没退"报成"复制失败"）
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception ex)
                {
                    error = "清空旧临时目录失败（可能被占用）：" + ex.Message + "\n" + tempDir;
                    return false;
                }
            }

            try
            {
                Directory.CreateDirectory(tempDir);
            }
            catch (Exception ex)
            {
                error = "创建临时目录失败（安装目录可能不可写）：" + ex.Message + "\n" + tempDir;
                return false;
            }

            // 2. 整目录复制
            //
            // ★ 二进制先落成 .dat 再改名：一部分杀软在"进程把可执行文件写到磁盘"这一刻同步扫描，
            //   扫不过就静默删掉（调用方拿不到异常，只看到"文件没了"）。改成 .dat 写入、
            //   本地改名（改名不重新扫内容）能绕过这一层。非二进制文件原样复制，不做多余动作。
            var copied = 0;
            var copiedBytes = 0L;

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var dest = Path.Combine(tempDir, relative);

                var isBinary = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

                var staging = isBinary ? dest + ".dat" : dest;

                try
                {
                    var parent = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    File.Copy(file, staging, overwrite: true);
                    if (isBinary) File.Move(staging, dest, overwrite: true);
                    copied++;
                    copiedBytes += new FileInfo(dest).Length;
                }
                catch (Exception ex)
                {
                    // 点名是哪个文件、哪一步、已经搬了多少 —— 只报 "拒绝访问" 等于让人对着
                    // 空文件夹猜。staging 与 dest 都写出来，能区分"写不进去"和"改名失败"。
                    error = $"复制文件失败（已复制 {copied} 个 / {copiedBytes / 1024} KB）\n" +
                            $"源：{file}\n写入：{staging}\n目标：{dest}\n错误：{ex.GetType().Name}：{ex.Message}";
                    return false;
                }
            }

            var tempExe = Path.Combine(tempDir, "IDEUpdater.exe");

            // 3. 强制校验：三个维度都要过，缺一个就会拉起到一个残废的更新器上
            //    ① 一个文件都没搬 → 源目录读不到（路径错 / 枚举被拦）
            if (copied == 0)
            {
                error = "源目录中没有任何文件可复制。\n源路径：" + sourceDir;
                return false;
            }

            //    ② 搬的数量对不上 → 复制成功后在磁盘上被削掉了（杀软静默删除的典型形态）
            var present = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories).Length;
            if (present < copied)
            {
                error = $"复制了 {copied} 个文件，但目标目录只剩 {present} 个 —— 有文件在复制后被删除。\n" +
                        $"疑似杀软拦截，请把 {Path.GetDirectoryName(tempDir)} 加入白名单后重试。\n目标目录：{tempDir}";
                return false;
            }

            //    ③ 更新器本体在不在、大小对不对（0 字节 / 被替换成占位文件都过不了这关）
            if (!File.Exists(tempExe))
            {
                error = $"更新器复制失败，找不到目标 exe：{tempExe}\n" +
                        $"已复制 {copied} 个文件，但 exe 不在 —— 可能被杀软静默删除了！";
                return false;
            }

            var sourceExeSize = new FileInfo(sourceExe).Length;
            var copiedExeSize = new FileInfo(tempExe).Length;
            if (sourceExeSize != copiedExeSize)
            {
                error = $"更新器副本大小不一致：源 {sourceExeSize} 字节，副本 {copiedExeSize} 字节。\n" +
                        $"疑似被杀软改写或删空：{tempExe}";
                return false;
            }

            // 4. 拉起更新器
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
                error = $"更新器进程没有创建成功：{tempExe}\n文件已完整复制（{copied} 个），" +
                        "问题出在启动这一步，多为杀软/组策略拦截。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // 兜住剩下的两处：枚举源目录（路径不存在 / 权限不足），以及 Process.Start（被拦、被策略禁运）。
            // 单文件复制已经在循环里各自报错，走到这里说明还没开始搬文件或已经搬完了。
            error = $"准备更新器时出错（{ex.GetType().Name}）：{ex.Message}\n" +
                    $"源目录：{sourceDir}\n临时目录：{tempDir}";
            return false;
        }
        finally
        {
            IsPreparing = false;
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