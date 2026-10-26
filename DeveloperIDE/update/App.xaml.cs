using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;

namespace IDEUpdater;

/// <summary>
/// DeveloperIDE 专属更新器：无窗口、纯代码，只做「转投 temp → 下载 → 校验 → 解压覆盖 → 拉起 → 自清理」。
/// 只用 .NET 自带能力（HttpClient / SHA256 / ZipFile / Process），不引任何第三方包。
///
/// ★ 金蝉脱壳是前提：更新器就住在 IDE 的安装目录里（与 DeveloperIDE.exe 并排），
///   就地运行会锁住自己的文件，替换阶段必然失败。所以先从 temp 拉起一份副本，
///   原进程立刻退出，再由副本干活。
/// </summary>
public partial class App : Application
{
    /// <summary>AssemblyName 就是 IDEUpdater，apphost 里写死的托管 dll 名与它一致，改名会起不来。</summary>
    const string UpdaterExeName = "IDEUpdater.exe";

    const string MainExeName = "DeveloperIDE.exe";

    /// <summary>下载地址同样是 Gitee raw：与非浏览器 UA 一样会被限速或挂起，这里对齐浏览器请求头。</summary>
    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>
    /// 搬去 temp 时要带上的文件：当前 exe 本体 + 同目录下所有 IDEUpdater.*。
    /// 单文件发布时只有 IDEUpdater.exe 一个；普通 build（多文件）时 dll / deps / runtimeconfig 一起跟上。
    /// </summary>
    static IEnumerable<string> SelfStageFiles()
    {
        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self)) yield return self;

        var dir = string.IsNullOrEmpty(self) ? AppContext.BaseDirectory : Path.GetDirectoryName(self) ?? AppContext.BaseDirectory;
        var pattern = Path.GetFileNameWithoutExtension(UpdaterExeName) + ".*";   // IDEUpdater.*
        foreach (var path in Directory.EnumerateFiles(dir, pattern))
        {
            if (!string.Equals(path, self, StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }

    /// <summary>用户产物绝对不碰；更新器本体（安装目录根）不在里面 —— 我们从 temp 跑，所以能覆盖它（才能更新自己）。</summary>
    static readonly string[] PreserveDirs = { "projects", "plugins", "data" };

    static readonly string[] PreserveFiles = { "ide_config.json" };

    /// <summary>
    /// 下载用的客户端：走系统代理（公司网络与 PAC 都靠它），超时给足 10 分钟 ——
    /// 几十 MB 的包按秒算会误杀。UA 在 CreateClient 里按浏览器设置，这里不再逐条加。
    /// </summary>
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        HttpClientHandler handler;
        try
        {
            // WebRequest 在 .NET 5+ 标了过时（SYSLIB0014），但显式取系统代理仍然最稳
#pragma warning disable SYSLIB0014
            handler = new HttpClientHandler
            {
                UseProxy = true,
                // 下载地址也可能 302（GitHub → objects.githubusercontent.com 之类），必须自动跟
                AllowAutoRedirect = true,
                Proxy = WebRequest.GetSystemWebProxy(),
                Credentials = CredentialCache.DefaultNetworkCredentials,
            };
#pragma warning restore SYSLIB0014
        }
        catch
        {
            handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = true };
        }

        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        // 别等 100-Continue：有些代理不回应这个请求头，会白等到超时
        client.DefaultRequestHeaders.ExpectContinue = false;
        return client;
    }

    string _logDir = Path.GetTempPath();
    bool _quiet;

    /// <summary>更新完要拉起的主程序文件名（<c>--exe</c> 传入，没给就用 IDEUpdater 旁边那份默认名）。</summary>
    string? _exeName;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = ParseArgs(e.Args);
        _quiet = args.ContainsKey("quiet");
        var url = Get(args, "url");
        var hash = Get(args, "hash");
        var target = Get(args, "target");
        var exe = Get(args, "exe");

        if (url is null || hash is null || target is null)
        {
            Fail("参数不完整：需要 --url、--hash、--target。");
            Environment.Exit(2);
            return;
        }

        _exeName = exe;
        _logDir = PickLogDir(target);
        Log("===== DeveloperIDE 更新开始 =====");

        // 不在 temp 里跑就先把自己搬出去，搬完立刻退出，剩下的活由 temp 副本做完
        if (!IsUnderTemp(AppContext.BaseDirectory) && TryRestage(url, hash, target)) return;

        string? error = null;
        try
        {
            RunUpdate(url, hash, target);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log("更新失败：" + ex);
        }

        CleanupSelf();

        if (error is null)
        {
            Log("===== 更新完成 =====");
            Environment.Exit(0);
        }

        Fail("更新失败：" + error);
        Environment.Exit(1);
    }

    // ─────────────────────────── 金蝉脱壳 ───────────────────────────

    /// <summary>
    /// 把自己搬到 <c>%Temp%\ClassDailyLand2.IDE.Update\</c> 再拉起副本，原进程立刻退出。
    /// 单文件发布时只复制一个 exe；多文件 build 时同目录的 IDEUpdater.* 一起跟上。
    /// </summary>
    bool TryRestage(string url, string hash, string target)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ClassDailyLand2.IDE.Update");
            Directory.CreateDirectory(dir);

            var copied = 0;
            foreach (var from in SelfStageFiles())
            {
                var to = Path.Combine(dir, Path.GetFileName(from));
                if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) continue;

                File.Copy(from, to, overwrite: true);
                copied++;
            }
            Log($"已复制 {copied} 个文件到 temp：{dir}");

            var staged = Path.Combine(dir, UpdaterExeName);
            if (!File.Exists(staged))
            {
                Log("temp 副本不存在，退回就地执行：" + staged);
                return false;
            }

            var arguments = $"--url \"{url}\" --hash \"{hash}\" --target \"{target}\"";
            if (!string.IsNullOrWhiteSpace(_exeName)) arguments += $" --exe \"{_exeName}\"";
            if (_quiet) arguments += " --quiet";

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = staged,
                Arguments = arguments,
                WorkingDirectory = dir,
                UseShellExecute = true,
            });

            Log(process is null
                ? "temp 副本启动失败，退回就地执行。"
                : $"已转投 temp 副本（PID {process.Id}），原进程退出。");

            if (process is null) return false;

            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            Log("转投 temp 出错，退回就地执行：" + ex.Message);
            return false;
        }
    }

    // ─────────────────────────── 更新主流程 ───────────────────────────

    void RunUpdate(string url, string hash, string target)
    {
        Log("等待 1.5 秒，让主程序彻底退出…");
        Thread.Sleep(1500);

        var installDir = Path.GetFullPath(target);
        Directory.CreateDirectory(installDir);

        var stamp = Guid.NewGuid().ToString("N")[..8];
        var zipPath = Path.Combine(Path.GetTempPath(), $"ide-update-{stamp}.zip");
        var payload = Path.Combine(Path.GetTempPath(), $"ide-payload-{stamp}");

        try
        {
            Download(url, zipPath);
            Verify(zipPath, hash);
            Extract(zipPath, payload);

            var (written, skipped) = Apply(payload, installDir);
            Log($"替换完成：写入 {written} 个文件，跳过 {skipped} 个。");

            LaunchMainApp(installDir);
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDir(payload);
        }
    }

    void Download(string url, string path)
    {
        Log("下载：" + url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");

        using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        Log(total > 0 ? $"预期大小：{total / 1024.0 / 1024.0:F1} MB" : "预期大小：未知（服务端没给 Content-Length）");

        using var remote = response.Content.ReadAsStream();
        using var local = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        remote.CopyTo(local, 1 << 16);
        local.Flush(true);

        Log($"下载完成：{new FileInfo(path).Length / 1024.0 / 1024.0:F1} MB");
    }

    /// <summary>哈希不匹配就整包拒绝 —— 下到一半、被 CDN 截断、被篡改都是这一关拦下。</summary>
    void Verify(string path, string expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var want = expected.Trim().ToLowerInvariant();

        Log("SHA256：" + actual);
        if (!string.Equals(actual, want, StringComparison.Ordinal))
            throw new InvalidDataException("SHA256 校验不一致（安装包不完整或被改动），已停止更新。");
    }

    /// <summary>解压到 temp 里的沙箱，先验条目名再落地 —— 防 Zip Slip 写穿到目录外。</summary>
    void Extract(string zipPath, string payload)
    {
        Directory.CreateDirectory(payload);
        var root = AppendSeparator(Path.GetFullPath(payload));

        using var archive = ZipFile.OpenRead(zipPath);
        Log($"压缩包条目：{archive.Entries.Count}");

        foreach (var entry in archive.Entries)
        {
            if (!TrySafeRelative(entry.FullName, out var relative))
                throw new InvalidDataException("包里有非法条目名，整包拒绝：" + entry.FullName);

            var dest = Path.GetFullPath(Path.Combine(root, relative));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("条目越出目标目录，整包拒绝：" + entry.FullName);

            var parent = Path.GetDirectoryName(dest);
            if (parent is not null) Directory.CreateDirectory(parent);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>把沙箱里的东西覆盖到安装目录，用户数据整棵子树跳过。</summary>
    (int Written, int Skipped) Apply(string payload, string installDir)
    {
        var written = 0;
        var skipped = 0;

        foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(payload, file);
            if (IsPreserved(relative))
            {
                skipped++;
                continue;
            }

            var dest = Path.Combine(installDir, relative);
            try
            {
                var parent = Path.GetDirectoryName(dest);
                if (parent is not null) Directory.CreateDirectory(parent);
                File.Copy(file, dest, overwrite: true);
                written++;
            }
            catch (Exception ex)
            {
                // 单个文件锁住（就地跑、杀软扫）不能让整个更新前功尽弃
                skipped++;
                Log($"跳过 {relative}：{ex.Message}");
            }
        }

        return (written, skipped);
    }

    void LaunchMainApp(string installDir)
    {
        // --exe 给的是文件名（常规）或相对路径（少数便携布局）
        var name = string.IsNullOrWhiteSpace(_exeName) ? MainExeName : _exeName!.Trim();
        var exe = Path.IsPathRooted(name) ? name : Path.Combine(installDir, name);

        if (!File.Exists(exe))
        {
            Log($"没找到 {name}，跳过启动。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir, UseShellExecute = true });
            Log("已启动更新后的 IDE。");
        }
        catch (Exception ex)
        {
            Log("启动 IDE 失败：" + ex.Message);
        }
    }

    // ─────────────────────────── 自清理 ───────────────────────────

    /// <summary>cmd 延迟几秒再删：此刻本进程正在退出，直接删会删到自己。</summary>
    void CleanupSelf()
    {
        var current = Trim(AppContext.BaseDirectory);
        var temp = Trim(Path.GetTempPath());
        if (!current.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return;

        // 摊在 %Temp% 根上跑（IDE 复制出来的 IDEUpdater_Temp.exe）：只能删自己这一个文件，绝不能碰整个 temp
        var singleFile = string.Equals(current, temp, StringComparison.OrdinalIgnoreCase);
        var target = singleFile ? (Environment.ProcessPath ?? "") : current;
        if (target.Length == 0) return;

        var command = singleFile ? $"del /f /q \"{target}\"" : $"rd /s /q \"{target}\"";

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & {command}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Log("已排程清理 temp 副本：" + target);
        }
        catch (Exception ex)
        {
            Log("清理排程失败（temp 里会留一份副本）：" + ex.Message);
        }
    }

    // ─────────────────────────── 杂项 ───────────────────────────

    /// <summary>
    /// 同时支持 <c>--url 值</c> 与 <c>--url=值</c> 两种写法：
    /// IDE 那边用 ProcessStartInfo.ArgumentList 传的是 <c>--url=...</c>，两种都得认，否则参数被丢。
    /// </summary>
    static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;

            var body = args[i][2..];
            var equal = body.IndexOf('=');
            if (equal >= 0)
            {
                map[body[..equal].Trim()] = body[(equal + 1)..].Trim();
                continue;
            }

            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            map[body.Trim()] = hasValue ? args[i + 1].Trim() : "";
            if (hasValue) i++;
        }
        return map;
    }

    static string? Get(Dictionary<string, string> args, string key) =>
        args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>日志写到 <c>&lt;target&gt;\ide-updater.log</c>（更新器与 IDE 同目录），出问题用户能自己翻。</summary>
    static string PickLogDir(string target)
    {
        try
        {
            var dir = Path.GetFullPath(target);
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return Path.GetTempPath();
        }
    }

    void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            File.AppendAllText(Path.Combine(_logDir, "ide-updater.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去绝不能影响更新
        }
    }

    void Fail(string message)
    {
        Log(message);
        if (_quiet) return;

        try
        {
            MessageBox.Show(message, "DeveloperIDE 更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 没有交互会话时弹不出来，日志已经写过
        }
    }

    static bool IsUnderTemp(string dir)
    {
        var current = Trim(dir);
        var temp = Trim(Path.GetTempPath());
        return current.Equals(temp, StringComparison.OrdinalIgnoreCase) ||
               current.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsPreserved(string relative)
    {
        var rel = relative.Replace('\\', '/').Trim('/');
        if (rel.Length == 0) return true;

        var slash = rel.IndexOf('/');
        var first = slash < 0 ? rel : rel[..slash];
        foreach (var dir in PreserveDirs)
            if (string.Equals(dir, first, StringComparison.OrdinalIgnoreCase)) return true;

        var leaf = Path.GetFileName(rel);
        foreach (var name in PreserveFiles)
            if (string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>zip 条目名的纯字符串闸门：拒绝绝对路径 / 盘符 / 数据流 / .. 段 / 保留设备名。</summary>
    static bool TrySafeRelative(string? entryName, out string relative)
    {
        relative = "";
        var text = (entryName ?? "").Trim().Replace('\\', '/');
        if (text.Length == 0) return false;
        if (text.StartsWith('/')) return false;                 // 绝对路径与 UNC
        if (text.Contains(':')) return false;                   // 盘符与 NTFS 数据流

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        foreach (var segment in segments)
        {
            if (segment == ".") continue;
            if (segment == "..") return false;
            if (IsReservedDeviceName(segment)) return false;
        }

        relative = string.Join('/', segments);
        return relative.Length > 0;
    }

    static bool IsReservedDeviceName(string segment)
    {
        var name = segment;
        var dot = name.IndexOf('.');
        if (dot >= 0) name = name[..dot];

        return name.ToUpperInvariant() switch
        {
            "CON" or "PRN" or "AUX" or "NUL" => true,
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" => true,
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9" => true,
            _ => false,
        };
    }

    static string AppendSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    static string Trim(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
