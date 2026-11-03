using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DeveloperTool.Market;

/// <summary>
/// 内嵌 OAuth 登录窗口：WebView2 打开授权页，页面跳到 redirect_uri 时在 NavigationStarting 里
/// 取消导航并从 URL 取 code/state——回调地址背后没有服务在听，也不占端口。
/// </summary>
public partial class OAuthWindow : Window
{
    readonly string _authorizeUrl;
    readonly string _redirectPrefix;
    readonly string _expectedState;

    /// <summary>授权码。窗口因取消 / 失败关闭时为 null，原因看 <see cref="Error"/>。</summary>
    public string? Code { get; private set; }

    /// <summary>失败原因（用户拒绝授权 / state 不匹配 / WebView2 起不来）。</summary>
    public string? Error { get; private set; }

    public OAuthWindow(string authorizeUrl, string redirectPrefix, string expectedState, CancellationToken ct)
    {
        InitializeComponent();
        _authorizeUrl = authorizeUrl;
        _redirectPrefix = redirectPrefix;
        _expectedState = expectedState;

        // 3 分钟超时（或调用方取消）→ 关窗，让 LoginAsync 走 OperationCanceledException
        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                try { Dispatcher.BeginInvoke(new Action(Close)); }
                catch (InvalidOperationException) { /* 进程已经在收尾 */ }
            });
        }
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CreateEnvironmentAsync();
            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            // 授权页里 target=_blank 的链接留在同一视图，别弹独立 WebView2 小窗
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                Browser.CoreWebView2.Navigate(args.Uri);
            };
            Browser.Source = new Uri(_authorizeUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // 只有官方 API 明确说「没有 runtime」才提示安装组件
            Error = "没找到可用的 WebView2 Runtime。" + RuntimeDiagnostics() + "\n" +
                    "请安装 Microsoft Edge WebView2 Runtime（Evergreen 版）后重试。";
            Close();
        }
        catch (Exception ex)
        {
            // 其它错误（权限 / COM / 目录不可写 …）原样报出来，别再一律说成「缺 runtime」
            Error = "登录页打不开（" + ex.GetType().Name + "，0x" + ex.HResult.ToString("X8") + "）：" + ex.Message;
            Close();
        }
    }

    /// <summary>进程级单例环境：同一个 UserDataFolder 只建一次，避免反复 Create 被判「目录占用」。</summary>
    static Task<CoreWebView2Environment>? _environmentTask;

    /// <summary>
    /// 建 Evergreen 环境：第一个参数必须传 null（用系统装的 runtime）。
    /// UserDataFolder 固定放 %LocalAppData%，让 Cookie / 缓存 / LocalStorage 落盘持久化——
    /// 一次登录 Gitee 后，重启 IDE 还是登录态。注意：WebView2 和系统 Edge/Chrome 的 Cookie
    /// 天然隔离（无法共享），这里做的是"让它自己的登录态跨重启留存"，不是"读系统浏览器的登录"。
    /// 目录真不可写（旧 ACL / 只读重定向）才退到 exe 同级，避免登录直接崩。
    /// </summary>
    static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        if (_environmentTask is not null) return await _environmentTask;

        var folder = ResolveUserDataFolder();

        // 机器上残留了错的 WEBVIEW2_BROWSER_EXECUTABLE_FOLDER，loader 会优先用它、直接报「找不到 runtime」
        var forced = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
        if (!string.IsNullOrEmpty(forced) && !File.Exists(Path.Combine(forced, "msedgewebview2.exe")))
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", null);

        if (!TryGetRuntimeVersion(out _))
        {
            // 兜底要指到「含 msedgewebview2.exe 的版本子目录」，指 Application 父目录不算数
            var found = FindRuntimeFolder();
            if (found is not null)
                Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", found);
        }

        var task = CoreWebView2Environment.CreateAsync(null, folder);
        _environmentTask = task;
        try
        {
            return await task;
        }
        catch
        {
            _environmentTask = null;   // 失败不缓存，下次点登录再试
            throw;
        }
    }

    /// <summary>
    /// 固定 UserDataFolder：优先 <c>%LocalAppData%\ClassDailyLand2\WebView2Data</c>
    /// （持久、跨 IDE 版本/重装稳定）。目录建得出来但写不进去（探针失败）时退到 exe 同级；
    /// 退出程序时不清空该目录，Cookie 才会留到下次启动。
    /// </summary>
    static string ResolveUserDataFolder()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(root))
            {
                var dir = Path.Combine(root, "ClassDailyLand2", "WebView2Data");
                Directory.CreateDirectory(dir);

                // 建得出来不代表写得进去（旧 ACL / 只读重定向），真写个探针文件才算数
                var probe = Path.Combine(dir, ".write_probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
        }
        catch
        {
            // 落到降级路径
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebView2Data");
    }

    /// <summary>官方探测：能拿到版本号才算真的可用（内部会看注册表 / 环境变量）。</summary>
    static bool TryGetRuntimeVersion(out string? version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            version = null;
            return false;
        }
    }

    /// <summary>报错时把现场信息带上，免得只看到一句「缺少 runtime」没法排查。</summary>
    static string RuntimeDiagnostics()
    {
        var version = TryGetRuntimeVersion(out var v) ? v : "未检测到";
        var forced = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
        return "\n进程架构：" + (Environment.Is64BitProcess ? "x64" : "x86") +
               "；运行时版本：" + version +
               (string.IsNullOrEmpty(forced) ? "" : "；环境变量指定：" + forced);
    }

    /// <summary>
    /// Evergreen 的固定落点（64 位系统上也在 Program Files (x86)），取版本号最大的子目录。
    /// 只在官方探测失败时用，平时绝不干预。
    /// </summary>
    static string? FindRuntimeFolder()
    {
        foreach (var root in new[]
                 {
                     @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application",
                     @"C:\Program Files\Microsoft\EdgeWebView\Application",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Microsoft", "EdgeWebView", "Application"),
                 })
        {
            if (!Directory.Exists(root)) continue;

            var latest = Directory.GetDirectories(root)
                .Select(dir => (Dir: dir, Ver: Version.TryParse(Path.GetFileName(dir), out var v) ? v : null))
                .Where(x => x.Ver is not null && File.Exists(Path.Combine(x.Dir, "msedgewebview2.exe")))
                .OrderByDescending(x => x.Ver)
                .Select(x => x.Dir)
                .FirstOrDefault();

            if (latest is not null) return latest;
        }
        return null;
    }

    void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        LoadingMask.Visibility = Visibility.Collapsed;

        // 只认回调地址：http://127.0.0.1:18080/callback?code=…&state=…
        if (!e.Uri.StartsWith(_redirectPrefix, StringComparison.OrdinalIgnoreCase)) return;

        // 立即取消：这个地址背后没有服务，绝不能真发请求
        e.Cancel = true;

        var uri = new Uri(e.Uri);
        var error = QueryValue(uri, "error");
        if (!string.IsNullOrEmpty(error))
        {
            Error = "授权被拒绝：" + (QueryValue(uri, "error_description") ?? error);
        }
        else if (QueryValue(uri, "code") is not { Length: > 0 } code)
        {
            Error = "回调里没有 code，授权没有完成。";
        }
        else if (QueryValue(uri, "state") != _expectedState)
        {
            Error = "回调 state 校验失败（可能不是本次授权），请重新登录。";
        }
        else
        {
            Code = code;
        }

        // 等 WebView2 的导航回调跑完再关窗，避免在事件里销毁控件
        Dispatcher.BeginInvoke(new Action(Close));
    }

    /// <summary>取 query 参数。code/state 都是 URL 安全字符，手解析即可，不引 System.Web。</summary>
    static string? QueryValue(Uri uri, string key)
    {
        var query = uri.Query;
        if (query.Length < 2) return null;
        foreach (var pair in query[1..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(Uri.UnescapeDataString(pair[..eq]), key, StringComparison.Ordinal)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        Browser.Dispose();   // 先放掉 CoreWebView2，再让窗口回收
        base.OnClosed(e);
    }
}
