using ClassDailyLand2.MarketContract;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeveloperTool.Market;

/// <summary>
/// 系统浏览器 OAuth：起一个只监听 127.0.0.1:18080 的极简 HTTP 服务，打开用户默认浏览器去授权，
/// 浏览器跳回 redirect_uri 时从请求行里取 code / state。
///
/// <para><b>为什么不用 HttpListener：</b>它走 http.sys，非管理员下绑定端口通常要 URL ACL 预留，
/// 一装就 AccessDenied。<see cref="TcpListener"/> 直接监听回环地址，普通权限就能用，
/// 也不需要用户装 WebView2 —— 这是「浏览器登录」相对内嵌窗的最大价值。</para>
///
/// <para><b>端口固定 18080：</b>OAuth 应用的回调地址是后端登记好的，换端口就授权失败。
/// 端口被占用时抛 <see cref="BrowserLoginUnavailableException"/>，调用方退回内嵌 WebView2。</para>
/// </summary>
internal static class BrowserLogin
{
    /// <summary>与后端登记的 redirect_uri 保持一致（改这里必须同步改 Gitee/GitHub 应用配置）。</summary>
    public const string RedirectUri = "http://127.0.0.1:18080/callback";

    const int Port = 18080;

    /// <summary>
    /// 打开浏览器并等待回调。拿到 code 返回；超时 / 用户取消抛 <see cref="OperationCanceledException"/>。
    /// 端口占不到、浏览器打不开抛 <see cref="BrowserLoginUnavailableException"/>（调用方据此降级）。
    /// </summary>
    public static async Task<string> RequestCodeAsync(
        string authorizeUrl, string expectedState, Action<string> log, CancellationToken ct)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new BrowserLoginUnavailableException(
                $"本地回调端口 {Port} 无法监听（{ex.SocketErrorCode}），可能被其它程序占用。", ex);
        }

        try
        {
            if (!TryOpenBrowser(authorizeUrl, log))
                throw new BrowserLoginUnavailableException("没能调起默认浏览器。");

            log($"已打开浏览器，请在页面里完成授权（{Port} 端口等待回调，3 分钟内有效）…");

            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var requestLine = await ReadRequestLineAsync(client, ct).ConfigureAwait(false);
                var path = ParsePath(requestLine);

                // 浏览器会顺手要 favicon：回 204 继续等，别当成回调
                if (path is null || path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(client, 204, "No Content", "", ct).ConfigureAwait(false);
                    continue;
                }

                if (!path.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(client, 404, "Not Found", "<h3>404</h3>", ct).ConfigureAwait(false);
                    continue;
                }

                // —— 真正的回调 ——
                var query = ParseQuery(path);

                if (query.TryGetValue("error", out var error) && error.Length > 0)
                {
                    var desc = query.TryGetValue("error_description", out var d) && d.Length > 0 ? d : error;
                    await RespondAsync(client, 200, "OK", ResultPage("授权未完成", desc, false), ct).ConfigureAwait(false);
                    throw new MarketApiException(0, "授权被拒绝：" + desc);
                }

                if (!query.TryGetValue("code", out var code) || code.Length == 0)
                {
                    await RespondAsync(client, 200, "OK", ResultPage("授权未完成", "回调里没有 code。", false), ct).ConfigureAwait(false);
                    throw new MarketApiException(0, "回调里没有 code，授权没有完成。");
                }

                if (!query.TryGetValue("state", out var state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    await RespondAsync(client, 200, "OK", ResultPage("校验失败", "state 不匹配，可能不是本次授权。", false), ct).ConfigureAwait(false);
                    throw new MarketApiException(0, "回调 state 校验失败（可能不是本次授权），请重新登录。");
                }

                await RespondAsync(client, 200, "OK", ResultPage("登录成功", "授权已完成，可以关闭这个页面回到 IDE。", true), ct).ConfigureAwait(false);
                return code;
            }
        }
        finally
        {
            try { listener.Stop(); } catch { /* 收尾失败无所谓 */ }
        }
    }

    static bool TryOpenBrowser(string url, Action<string> log)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            log("调起浏览器失败：" + ex.Message);
            return false;
        }
    }

    static async Task<string?> ReadRequestLineAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read <= 0) return null;

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            return lineEnd > 0 ? text[..lineEnd] : text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 "GET /callback?code=.. HTTP/1.1" 里取路径+查询串。</summary>
    static string? ParsePath(string? requestLine)
    {
        if (string.IsNullOrWhiteSpace(requestLine)) return null;
        var parts = requestLine.Split(' ');
        return parts.Length >= 2 ? parts[1] : null;
    }

    static Dictionary<string, string> ParseQuery(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = path.IndexOf('?');
        if (q < 0 || q == path.Length - 1) return map;

        foreach (var pair in path[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            map[key] = value;
        }
        return map;
    }

    static async Task RespondAsync(TcpClient client, int status, string reason, string html, CancellationToken ct)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");

            using var stream = client.GetStream();
            await stream.WriteAsync(head, ct).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 浏览器可能提前断开，忽略
        }
    }

    /// <summary>回调页：深色卡片 + 一句结论，用户看到的就是这个页面。</summary>
    static string ResultPage(string title, string message, bool ok)
    {
        var accent = ok ? "#3DDC84" : "#FF7A7A";
        var icon = ok ? "✓" : "!";
        return $@"<!doctype html>
<html lang=""zh-CN""><head><meta charset=""utf-8"">
<title>Class Daily Land 2 · {title}</title>
<style>
  :root {{ color-scheme: light dark; }}
  body {{ margin:0; min-height:100vh; display:grid; place-items:center;
         font-family: 'Segoe UI', 'Microsoft YaHei UI', system-ui, sans-serif;
         background:#0f1115; color:#e8eaed; }}
  .card {{ width:min(420px, 88vw); padding:34px 32px; border-radius:18px;
           background:#171a21; border:1px solid #262b36; text-align:center;
           box-shadow:0 24px 60px rgba(0,0,0,.45); }}
  .badge {{ width:56px; height:56px; margin:0 auto 18px; border-radius:16px;
            display:grid; place-items:center; font-size:26px; font-weight:700;
            color:#0f1115; background:{accent}; }}
  h1 {{ font-size:19px; margin:0 0 10px; font-weight:600; }}
  p {{ margin:0; font-size:13.5px; line-height:1.7; color:#9aa3b2; }}
</style></head>
<body><div class=""card"">
  <div class=""badge"">{icon}</div>
  <h1>{title}</h1>
  <p>{message}</p>
</div></body></html>";
    }
}

/// <summary>浏览器登录不可用（端口占用 / 浏览器打不开）：调用方据此退回内嵌 WebView2。</summary>
internal sealed class BrowserLoginUnavailableException : Exception
{
    public BrowserLoginUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
