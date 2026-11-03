using ClassDailyLand2.Core.Plugins;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace ClassDailyLand2.Core.Market;

/// <summary>下载结果。<see cref="Path"/> 是**校验通过**的安装包临时路径。</summary>
public sealed record MarketDownload(bool Ok, string? Path, string? Error, string? Source, string? Sha256)
{
    public static MarketDownload Fail(string error) => new(false, null, error, null, null);
}

/// <summary>
/// 下载 + 三道校验。市场与「手动装 zip」的唯一区别就在这里，缺一道就等于没有市场：
///
/// <list type="number">
///   <item><b>哈希</b>：与索引里的 sha256 / size 比对，不符直接删文件；</item>
///   <item><b>身份</b>：包内 <c>plugin.json</c> 的 id / version 必须等于索引条目
///         —— 防「索引挂 1.2、包里是 1.0」；</item>
///   <item><b>形状</b>：包内必须有 entry 指向的 dll，且不许带契约 dll；</item>
/// </list>
///
/// 三道都过才把临时路径交给 <c>PluginInstaller</c>，安装那一步的目录覆盖/回滚逻辑不重复实现。
/// </summary>
public static class MarketDownloader
{
    /// <summary>与 PluginInstaller 同一个上限，避免"下载 500MB 才发现装不了"。</summary>
    const long MaxBytes = 64L * 1024 * 1024;

    const string ContractDll = "classdailyland2.plugin.abstractions.dll";

    /// <summary>包下载比取索引慢，给足 3 分钟。</summary>
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>临时目录：每次安装一个独立子目录，装完/失败都删掉。</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "ClassDailyLand2.Market");

    public static async Task<MarketDownload> DownloadAsync(
        MarketEntry entry,
        IReadOnlyList<string> rawRoots,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var urls = MarketClient.PackageUrls(entry, rawRoots);
        if (urls.Count == 0) return MarketDownload.Fail("索引里没有可用的安装包地址。");

        var workDir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        var target = Path.Combine(workDir, entry.Id + ".cblplugin");
        var errors = new List<string>();

        try
        {
            Directory.CreateDirectory(workDir);

            foreach (var url in urls)
            {
                var source = MarketClient.SourceName(url);
                ClassDailyLand2.Core.Logger.Current?.Trace($"[市场] 下载 {entry.Id} v{entry.Version}（{source}，期望 {entry.Size} 字节）：{url}");
                try
                {
                    progress?.Report(0);
                    await DownloadOneAsync(url, target, entry.Size, progress, ct).ConfigureAwait(false);

                    var digest = ComputeSha256(target);
                    if (!string.Equals(digest, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"{source}：哈希不符（得到 {digest[..12]}…，索引写的是 {entry.Sha256[..Math.Min(12, entry.Sha256.Length)]}…）");
                        TryDelete(target);
                        continue;
                    }

                    var problem = InspectPackage(target, entry);
                    if (problem is not null)
                    {
                        errors.Add($"{source}：{problem}");
                        TryDelete(target);
                        continue;
                    }

                    progress?.Report(1);
                    // ★ 成功时**不能**删 workDir：临时包路径要交给 PluginInstaller，
                    //   调用方（MarketService）装完再清（见 CleanupPackage）。
                    return new MarketDownload(true, target, null, source, digest);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDeleteDirectory(workDir);
                    return MarketDownload.Fail("已取消");
                }
                catch (TaskCanceledException)
                {
                    errors.Add($"{source}：下载超时");
                    TryDelete(target);
                }
                catch (Exception ex)
                {
                    errors.Add($"{source}：{Describe(ex)}");
                    TryDelete(target);
                }
            }

            // 所有源都失败：把每个源的原因都带回去，不然用户只知道"装不上"
            TryDeleteDirectory(workDir);
            return MarketDownload.Fail(string.Join("；", errors));
        }
        catch
        {
            TryDeleteDirectory(workDir);
            throw;
        }
    }

    /// <summary>装完（或装失败）后清掉临时目录 —— 与 <see cref="DownloadAsync"/> 的成功路径配对。</summary>
    public static void CleanupPackage(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(packagePath);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* 清不掉下次启动还会被 TempRoot 清一遍 */ }
    }

    /// <summary>启动时兜底清理：上次崩溃留下的半截包不该一直占着临时目录。</summary>
    public static void CleanupStale()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.GetDirectories(TempRoot))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* 正在用的那个删不掉，正常 */ }
            }
        }
        catch { /* 清理失败不影响任何功能 */ }
    }

    // ————————————————————— 下载 —————————————————————

    static async Task DownloadOneAsync(string url, string file, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, MarketClient.WithCacheBuster(url));
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, must-revalidate");

        using var response = await MarketClient.Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

        var total = response.Content.Headers.ContentLength ?? expectedSize;
        if (total > MaxBytes) throw new InvalidOperationException($"安装包 {total / 1024 / 1024} MB，超过 {MaxBytes / 1024 / 1024} MB 上限");

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await using var target = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (read <= 0) break;

            received += read;
            if (received > MaxBytes)
                throw new InvalidOperationException($"安装包超过 {MaxBytes / 1024 / 1024} MB 上限");

            await target.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
            if (total > 0) progress?.Report(Math.Clamp(received / (double)total, 0, 0.99));
        }
    }

    // ————————————————————— 校验 —————————————————————

    /// <summary>打开 zip 逐项核对；返回 null 表示这包能用，否则返回人话原因。</summary>
    public static string? InspectPackage(string packagePath, MarketEntry entry)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(packagePath);
        }
        catch (InvalidDataException)
        {
            return "下载到的不是有效压缩包（可能被代理/网关替换过）";
        }

        using (zip)
        {
            var names = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/'))
                .ToList();

            if (!names.Contains("plugin.json")) return "包里没有 plugin.json";

            var contract = names.FirstOrDefault(n =>
                string.Equals(Path.GetFileName(n), ContractDll, StringComparison.OrdinalIgnoreCase));
            if (contract is not null) return $"包里带了契约程序集（{contract}），应由宿主提供";

            string json;
            var manifestEntry = zip.GetEntry("plugin.json");
            if (manifestEntry is null) return "包里没有 plugin.json";
            using (var reader = new StreamReader(manifestEntry.Open()))
                json = reader.ReadToEnd();

            if (!PluginManifest.TryParse(json, "", out var manifest, out var error))
                return "包内清单不合法：" + (error ?? "未知原因");

            if (!string.Equals(manifest.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                return $"包内 id「{manifest.Id}」与索引「{entry.Id}」不一致";

            if (!string.Equals(manifest.Version, entry.Version, StringComparison.OrdinalIgnoreCase))
                return $"包内 version「{manifest.Version}」与索引「{entry.Version}」不一致";

            if (!names.Contains(manifest.Entry))
                return $"包里找不到入口程序集 {manifest.Entry}";

            return null;
        }
    }

    public static string ComputeSha256(string file)
    {
        using var stream = File.OpenRead(file);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return ReferenceEquals(inner, ex)
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.Message} → {inner.Message}";
    }

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* 删不掉不影响主流程 */ }
    }

    static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* 同上 */ }
    }
}
