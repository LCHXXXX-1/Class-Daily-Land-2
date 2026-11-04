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
                    await RespondAsync(client, 200, "OK", ResultPage("授权未完成", "回调里没有拿到 code，授权没有完成，请回到 IDE 重新登录。", false), ct).ConfigureAwait(false);
                    throw new MarketApiException(0, "回调里没有 code，授权没有完成。");
                }

                if (!query.TryGetValue("state", out var state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    await RespondAsync(client, 200, "OK", ResultPage("校验失败", "state 不匹配，可能不是本次授权，请回到 IDE 重新登录。", false), ct).ConfigureAwait(false);
                    throw new MarketApiException(0, "回调 state 校验失败（可能不是本次授权），请重新登录。");
                }

                await RespondAsync(client, 200, "OK", ResultPage("登录成功", "授权已完成，可以直接关闭这个页面回到 IDE。", true), ct).ConfigureAwait(false);
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

        /// <summary>
    /// 回调页：用户在浏览器里看到的结论页。
    /// 成功时给出「可以直接关闭」的明确提示，并尝试自动关闭标签页（浏览器通常会拦，
    /// 那就把提示改成手动关闭方式）；失败时说明原因并提示回到 IDE 重试。
    /// </summary>
    static string ResultPage(string title, string message, bool ok)
    {
        var accent = ok ? "#2ED573" : "#FF6B6B";
        var accentSoft = ok ? "rgba(46,213,115,.14)" : "rgba(255,107,107,.14)";

        // 成功：描边勾；失败：感叹号
        var badge = ok
            ? "<svg viewBox=\"0 0 48 48\" aria-hidden=\"true\"><path d=\"M13 25.5l7.5 7.5L35 16\"/></svg>"
            : "<span class=\"mark\">!</span>";

        var autoClose = ok
            ? "      // 浏览器一般不允许脚本关闭\"用户手动打开\"的标签页：先试一次，\n" +
              "      // 关不掉就把提示换成手动关闭方式，免得用户以为页面卡住了。\n" +
              "      setTimeout(function () {\n" +
              "        closeTab();\n" +
              "        setTimeout(function () {\n" +
              "          if (!document.hidden) {\n" +
              "            hint.textContent = '浏览器阻止了自动关闭：按 Ctrl+W，或点击上面的按钮即可。';\n" +
              "          }\n" +
              "        }, 600);\n" +
              "      }, 2200);"
            : "      // 失败时不自动关闭：用户可能要看清原因。";

        var hint = ok ? "正在尝试自动关闭此页面…" : "可以关闭此页面，回到 IDE 重新登录。";

        var html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Class Daily Land 2 · {{TITLE}}</title>
<style>
  :root {
    color-scheme: light dark;
    --accent: {{ACCENT}};
    --accent-soft: {{ACCENT_SOFT}};
    --bg: #0c0e12;
    --bg-glow: #161b22;
    --card: #171a21;
    --border: #262b36;
    --text: #e9ecf1;
    --muted: #98a1b0;
    --btn: #1f232c;
    --btn-hover: #282d38;
    --shadow: 0 26px 70px rgba(0,0,0,.45);
  }
  @media (prefers-color-scheme: light) {
    :root {
      --bg: #f4f5f8;
      --bg-glow: #ffffff;
      --card: #ffffff;
      --border: #e3e6eb;
      --text: #14171c;
      --muted: #5f6875;
      --btn: #f1f3f6;
      --btn-hover: #e7eaef;
      --shadow: 0 22px 55px rgba(20,23,28,.12);
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh; padding: 24px;
    display: grid; place-items: center;
    color: var(--text);
    font-family: 'Segoe UI', 'Microsoft YaHei UI', system-ui, -apple-system, sans-serif;
    -webkit-font-smoothing: antialiased;
    background:
      radial-gradient(120% 85% at 50% -10%, var(--accent-soft), transparent 60%),
      radial-gradient(90% 70% at 50% 110%, var(--bg-glow), transparent 65%),
      var(--bg);
  }
  .card {
    width: min(440px, 100%); padding: 40px 34px 26px; border-radius: 22px;
    background: var(--card); border: 1px solid var(--border);
    box-shadow: var(--shadow); text-align: center;
    animation: rise .36s cubic-bezier(.2,.8,.3,1) both;
  }
  @keyframes rise {
    from { opacity: 0; transform: translateY(12px) scale(.985); }
    to   { opacity: 1; transform: none; }
  }
  .badge {
    width: 66px; height: 66px; margin: 0 auto 22px; border-radius: 21px;
    display: grid; place-items: center;
    background: var(--accent-soft);
    border: 1px solid color-mix(in srgb, var(--accent) 38%, transparent);
  }
  .badge svg {
    width: 36px; height: 36px; fill: none;
    stroke: var(--accent); stroke-width: 4;
    stroke-linecap: round; stroke-linejoin: round;
    stroke-dasharray: 46; stroke-dashoffset: 46;
    animation: draw .5s .18s cubic-bezier(.4,.6,.25,1) forwards;
  }
  @keyframes draw { to { stroke-dashoffset: 0; } }
  .badge .mark { font-size: 31px; font-weight: 800; color: var(--accent); line-height: 1; }
  h1 { margin: 0 0 10px; font-size: 20px; font-weight: 650; letter-spacing: .2px; }
  p { margin: 0; font-size: 13.5px; line-height: 1.75; color: var(--muted); }
  .actions { margin-top: 26px; display: flex; gap: 10px; justify-content: center; }
  button {
    font: inherit; font-size: 13px; padding: 9px 22px; border-radius: 10px;
    color: var(--text); background: var(--btn);
    border: 1px solid var(--border); cursor: pointer;
    transition: background .16s ease, transform .12s ease;
  }
  button:hover { background: var(--btn-hover); }
  button:active { transform: translateY(1px); }
  .hint { margin-top: 14px; min-height: 17px; font-size: 11.5px; color: var(--muted); opacity: .85; }
  .brand {
    margin-top: 22px; padding-top: 16px; border-top: 1px solid var(--border);
    font-size: 10.5px; letter-spacing: 1.1px; text-transform: uppercase;
    color: var(--muted); opacity: .5;
  }
</style>
</head>
<body>
  <main class="card">
    <div class="badge">{{BADGE}}</div>
    <h1>{{TITLE}}</h1>
    <p>{{MESSAGE}}</p>
    <div class="actions"><button id="closeBtn" type="button">关闭此页面</button></div>
    <div class="hint" id="hint">{{HINT}}</div>
    <div class="brand">Class Daily Land 2 · 插件市场</div>
  </main>
  <script>
  (function () {
    var hint = document.getElementById('hint');

    function closeTab() {
      try { window.open('', '_self'); } catch (e) {}
      try { window.close(); } catch (e) {}
    }

    document.getElementById('closeBtn').addEventListener('click', closeTab);

{{AUTO_CLOSE}}
  })();
  </script>
</body>
</html>
""";

        return html
            .Replace("{{ACCENT}}", accent)
            .Replace("{{ACCENT_SOFT}}", accentSoft)
            .Replace("{{BADGE}}", badge)
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title))
            .Replace("{{MESSAGE}}", System.Net.WebUtility.HtmlEncode(message))
            .Replace("{{HINT}}", System.Net.WebUtility.HtmlEncode(hint))
            .Replace("{{AUTO_CLOSE}}", autoClose);
    }
}

/// <summary>浏览器登录不可用（端口占用 / 浏览器打不开）：调用方据此退回内嵌 WebView2。</summary>
internal sealed class BrowserLoginUnavailableException : Exception
{
    public BrowserLoginUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
