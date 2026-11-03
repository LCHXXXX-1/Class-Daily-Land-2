using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassDailyLand2.MarketContract;

using DeveloperTool.Core;

namespace DeveloperTool.Market;

/// <summary>
/// 插件市场登录态：OAuth 桌面端授权码流程 + JWT 的 DPAPI 加密持久化。
///
/// HTTP 细节全在共享的 <see cref="MarketApiClient"/>（内部静态共享 HttpClient，不会耗尽 Socket）；
/// 这里只负责「拿到 code → 换 Token → 存取」和登录态。强制改名对话框由 MainWindow 驱动，
/// 服务只暴露 <see cref="SetUsernameAsync"/>。
/// </summary>
public sealed class MarketAuthService
{
    /// <summary>★ 与 Gitee/GitHub 后台回调、服务器 .env 三处必须一字不差（API.md §6，用 127.0.0.1 避开 IPv6 坑）。</summary>
    public const string RedirectUri = "http://127.0.0.1:18080/callback";

    // ★ client_id 不是机密，但必须与服务器 .env 里的一致；接入时填进来（见验证项）
    const string GiteeClientId = "cd440b157924415716ec4ba5bc484e7deff0e09bea0a866e8bd5fe62b696bead";
    const string GitHubClientId = "Ov23liSjjLOdKBucqgKe";

    /// <summary>
    /// 授权文件落点：<see cref="WritableData.Dir"/>（优先 %AppData%，不可写时退 exe 同级），
    /// 免得一个只读的配置目录把登录打回失败。
    /// </summary>
    static string StorePath => Path.Combine(WritableData.Dir, "ide_auth.json");

    static readonly TimeSpan LoginDeadline = TimeSpan.FromMinutes(3);

    public MarketApiClient Api { get; } = new();
    public string? Provider { get; private set; }
    public MarketUser? User { get; private set; }
    public bool IsLoggedIn => Api.Token is not null && User is not null;

    public Task<MarketLoginResult> SetUsernameAsync(string username, CancellationToken ct = default) =>
        Api.SetUsernameAsync(username, ct);

    // ————————————————————— OAuth 授权码流程 —————————————————————

    /// <summary>
    /// 弹内嵌 WebView2 登录窗口 → 拦截回调拿 code → 换 Token 并持久化。
    /// 超时 / 用户关窗抛 <see cref="OperationCanceledException"/>，授权失败抛 <see cref="MarketApiException"/>。
    /// </summary>
    public async Task<MarketLoginResult> LoginAsync(string provider, Action<string> log, CancellationToken ct = default)
    {
        var clientId = provider switch
        {
            "github" => GitHubClientId,
            "gitee" => GiteeClientId,
            _ => throw new MarketApiException(0, $"不支持的登录平台：{provider}"),
        };
        if (string.IsNullOrWhiteSpace(clientId))
            throw new MarketApiException(0, $"{provider} 的 client_id 还没配置（MarketAuthService 顶部常量）");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(LoginDeadline);

        var code = RequestCode(provider, clientId, log, deadline.Token);

        log("收到授权码，向服务器换取 Token…");

        // Api.LoginAsync 内部会把 Token 赋给 Api.Token，后续请求自动注入
        var result = await Api.LoginAsync(code, provider, deadline.Token).ConfigureAwait(false);
        Provider = provider;
        User = result.User;
        Save(log);
        return result;
    }

    /// <summary>
    /// 绑定另一个平台：同一个 WebView2 授权窗拿 code，再调 POST /api/auth/bind。
    /// 后端可能把「单平台占用的旧号」合并删掉，所以返回绑定后的用户。
    /// </summary>
    public async Task<MarketUser> BindAsync(string provider, Action<string> log, CancellationToken ct = default)
    {
        var clientId = provider switch
        {
            "github" => GitHubClientId,
            "gitee" => GiteeClientId,
            _ => throw new MarketApiException(0, $"不支持的登录平台：{provider}"),
        };
        if (string.IsNullOrWhiteSpace(clientId))
            throw new MarketApiException(0, $"{provider} 的 client_id 还没配置（MarketAuthService 顶部常量）");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(LoginDeadline);

        var code = RequestCode(provider, clientId, log, deadline.Token);

        var result = await Api.BindAsync(provider, code, deadline.Token).ConfigureAwait(false);
        if (result.User is not null) User = result.User;
        Save(log);
        return User ?? throw new MarketApiException(0, "绑定成功，但服务器没返回用户信息");
    }

    /// <summary>
    /// 弹内嵌 WebView2 授权窗，从回调里取 code（登录 / 绑定共用）。
    /// 超时或关窗抛 <see cref="OperationCanceledException"/>，授权失败抛 <see cref="MarketApiException"/>。
    /// </summary>
    static string RequestCode(string provider, string clientId, Action<string> log, CancellationToken deadline)
    {
        // state 防 CSRF：回调 URL 里带回来的必须是刚发出去的那个随机串
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        // 内嵌 WebView2 走授权页：不占端口、不开外部浏览器，管理员权限下也不会和 Edge 抢权限
        var window = new OAuthWindow(BuildAuthorizeUrl(provider, clientId, state), RedirectUri, state, deadline);
        if (System.Windows.Application.Current is { MainWindow: { IsLoaded: true } owner }) window.Owner = owner;

        log("已打开登录窗口等待授权（3 分钟内有效）…");
        window.ShowDialog();

        var code = window.Code;
        if (!string.IsNullOrEmpty(code)) return code;

        if (deadline.IsCancellationRequested) throw new OperationCanceledException(deadline);
        if (window.Error is { Length: > 0 } error) throw new MarketApiException(0, error);
        throw new OperationCanceledException();     // 用户直接关窗＝取消
    }

    static string BuildAuthorizeUrl(string provider, string clientId, string state)
    {
        var redirect = Uri.EscapeDataString(RedirectUri);
        return provider switch
        {
            // GitHub 的 scope 留空：后端只用 code 换身份，不需要仓库权限
            "github" => $"https://github.com/login/oauth/authorize?client_id={clientId}&redirect_uri={redirect}&scope=read:user&state={state}",
            "gitee" => $"https://gitee.com/oauth/authorize?client_id={clientId}&redirect_uri={redirect}&response_type=code&state={state}",
            _ => throw new ArgumentException(provider),
        };
    }

    // ————————————————————— 登录态存取 —————————————————————

    /// <summary>启动时调用：解出上次的 Token。解不开（换 Windows 账号 / 文件被改）就当没登录。</summary>
    public void LoadStored()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var store = JsonSerializer.Deserialize<AuthStore>(File.ReadAllText(StorePath), MarketJson.Options);
            if (store is null || string.IsNullOrEmpty(store.ProtectedToken)) return;

            Api.Token = Encoding.UTF8.GetString(DpapiProtector.Unprotect(Convert.FromBase64String(store.ProtectedToken)));
            Provider = store.Provider;
            User = new MarketUser { Id = store.UserId, Username = store.Username };
        }
        catch
        {
            SignOut();
        }
    }

    /// <summary>
    /// 用存的 Token 拉一次最新用户（用户名可能改过）。401 = Token 过期（JWT 无撤销也不会自动续期）、
    /// 403 = 上次没改完用户名：都退回未登录。断网 / 5xx 不判定失效，保持现状。
    /// </summary>
    public async Task RefreshStoredAsync()
    {
        if (!IsLoggedIn) return;
        try
        {
            User = await Api.GetMeAsync().ConfigureAwait(false);
            Save();
        }
        catch (MarketApiException ex) when (ex.StatusCode is 401 or 403)
        {
            SignOut();
        }
        catch
        {
            // 网络问题不当成登录失效
        }
    }

    public void SignOut()
    {
        Api.Token = null;
        Provider = null;
        User = null;
        try { if (File.Exists(StorePath)) File.Delete(StorePath); }
        catch { /* 删不掉也不拦退出 */ }
    }

    void Save(Action<string>? log = null)
    {
        if (Api.Token is null || User is null) return;

        try
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var protectedToken = Convert.ToBase64String(
                DpapiProtector.Protect(Encoding.UTF8.GetBytes(Api.Token)));
            File.WriteAllText(path, JsonSerializer.Serialize(
                new AuthStore(Provider ?? "", User.Username, User.Id, protectedToken),
                MarketJson.Options));
        }
        catch (Exception ex)
        {
            // 落盘失败只影响「下次免登录」，不能把已经成功的登录打回失败
            log?.Invoke("警告：登录状态保存失败（下次需要重新登录）：" + ex.Message);
        }
    }

    sealed record AuthStore(string Provider, string Username, long UserId, string ProtectedToken);
}

/// <summary>
/// DPAPI（CurrentUser）加密。.NET 里 ProtectedData 是独立 NuGet 包，按项目「不新增依赖」
/// 的约定直接 P/Invoke crypt32 调系统 API（与 NativeProcess.cs 同一思路）。
/// 只在当前 Windows 账号下能解开，跨机器 / 换账号复制过去都解不开。
/// </summary>
static class DpapiProtector
{
    const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public IntPtr CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? dataDescr,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr dataDescrOut,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr handle);

    public static byte[] Protect(byte[] data)
    {
        var input = Alloc(data);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Token 加密失败（DPAPI）");
            return CopyAndFree(output);
        }
        finally
        {
            Free(input);
        }
    }

    public static byte[] Unprotect(byte[] data)
    {
        var input = Alloc(data);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Token 解密失败（DPAPI）");
            return CopyAndFree(output);
        }
        finally
        {
            Free(input);
        }
    }

    static DataBlob Alloc(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { CbData = (IntPtr)data.Length, PbData = pointer };
    }

    static byte[] CopyAndFree(DataBlob blob)
    {
        try
        {
            var bytes = new byte[blob.CbData.ToInt64()];
            Marshal.Copy(blob.PbData, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            LocalFree(blob.PbData);
        }
    }

    static void Free(DataBlob blob) => LocalFree(blob.PbData);
}
