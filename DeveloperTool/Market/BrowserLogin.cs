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
    /// 成功时显示「登录成功」+ 图标外圈的圆形倒计时（纯 CSS 动画，逐帧平滑），
    /// 环走完再柔和淡出并尝试关闭标签页；被浏览器拦下时恢复页面并提示手动关闭。
    /// </summary>
    static string ResultPage(string title, string message, bool ok)
    {
        var accent = ok ? "#2ED573" : "#FF6B6B";
        var accentSoft = ok ? "rgba(46,213,115,.14)" : "rgba(255,107,107,.14)";

        // 成功：描边勾（配 pathLength=100，描边进度才准确）；失败：感叹号
        var badge = ok
            ? "<svg class=\"check\" viewBox=\"0 0 48 48\" aria-hidden=\"true\"><path pathLength=\"100\" d=\"M13 25.5l7.5 7.5L35 16\"/></svg>"
            : "<span class=\"mark\">!</span>";

        // 倒计时环只出现在成功页
        var ring = ok
            ? "<svg class=\"ring\" viewBox=\"0 0 72 72\" aria-hidden=\"true\">" +
              "<circle cx=\"36\" cy=\"36\" r=\"34\" pathLength=\"100\" /></svg>"
            : "";

        var hint = ok ? "3 秒后自动关闭此页面…" : "可以关闭此页面，回到 IDE 重新登录。";

        var autoClose = ok
            ? """
      // —— 倒计时：环走完 = 时间到（CSS 动画与视觉天然同步，不做逐帧算位） ——
      var ringCircle = document.querySelector('.ring circle');

      // 只在"整秒"变化时改文字：每帧写 DOM 会引发布局抖动，看起来就"卡"
      var totalSec = Math.round(DURATION / 1000);
      var startedAt = performance.now();
      var lastSecond = -1;
      function tick() {
        if (closing) return;
        var left = Math.max(0, DURATION - (performance.now() - startedAt));
        var sec = Math.min(totalSec, Math.ceil(left / 1000));
        if (sec !== lastSecond) {
          lastSecond = sec;
          hintText.textContent = sec > 0 ? sec + ' 秒后自动关闭此页面…' : '正在关闭此页面…';
        }
        requestAnimationFrame(tick);
      }
      requestAnimationFrame(tick);

      // 主轴：环画完就关；兜底：万一动画事件没来（标签页被节流）也照关
      if (ringCircle) ringCircle.addEventListener('animationend', beginClose, { once: true });
      var fallback = setTimeout(beginClose, DURATION + 600);
"""
            : """
      // 失败时不自动关闭：用户可能要看清原因。
""";

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
    will-change: opacity;
    /* 关闭前用这个过渡柔和淡出，而不是让页面"啪"地消失 */
    transition: opacity .26s ease;
    animation: rise .5s cubic-bezier(.16,1,.3,1) both;
  }
  .card.closing { opacity: 0; }
  @keyframes rise {
    from { opacity: 0; transform: translateY(14px) scale(.982); }
    to   { opacity: 1; transform: none; }
  }
  .badge-wrap { position: relative; width: 66px; height: 66px; margin: 0 auto 22px; }
  .badge {
    position: absolute; inset: 0; border-radius: 21px;
    display: grid; place-items: center;
    background: var(--accent-soft);
    border: 1px solid color-mix(in srgb, var(--accent) 38%, transparent);
    animation: pop .55s .06s cubic-bezier(.34,1.42,.48,1) both;
  }
  @keyframes pop {
    from { transform: scale(.72); opacity: 0; }
    to   { transform: scale(1); opacity: 1; }
  }
  /* 倒计时环：绕在图标外圈，从满到空匀速走完 */
  .ring {
    position: absolute; inset: -5px; width: 76px; height: 76px;
    transform: rotate(-90deg); transform-origin: 50% 50%;
    pointer-events: none;
    transition: opacity .2s ease;
  }
  .ring circle {
    fill: none; stroke: var(--accent); stroke-width: 2.6;
    stroke-linecap: round; stroke-dasharray: 100; stroke-dashoffset: 0;
    opacity: .8;
    animation: countdown {{DURATION_S}}s linear forwards;
  }
  @keyframes countdown { to { stroke-dashoffset: 100; } }
  .check {
    width: 36px; height: 36px; fill: none;
    stroke: var(--accent); stroke-width: 4;
    stroke-linecap: round; stroke-linejoin: round;
    stroke-dasharray: 100; stroke-dashoffset: 100;
    animation: draw .42s .2s cubic-bezier(.62,0,.36,1) forwards;
  }
  @keyframes draw { to { stroke-dashoffset: 0; } }
  .mark { font-size: 31px; font-weight: 800; color: var(--accent); line-height: 1; }
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
  .hint {
    margin-top: 14px; min-height: 17px; font-size: 11.5px;
    color: var(--muted); opacity: .85;
    font-variant-numeric: tabular-nums;   /* 秒数变化时宽度不跳 */
  }
  .brand {
    margin-top: 22px; padding-top: 16px; border-top: 1px solid var(--border);
    font-size: 10.5px; letter-spacing: 1.1px; text-transform: uppercase;
    color: var(--muted); opacity: .5;
  }
  @media (prefers-reduced-motion: reduce) {
    .card, .badge, .check, .ring circle { animation-duration: .01s; }
  }
</style>
</head>
<body>
  <main class="card" id="card">
    <div class="badge-wrap">
      <div class="badge">{{BADGE}}</div>
      {{RING}}
    </div>
    <h1>{{TITLE}}</h1>
    <p>{{MESSAGE}}</p>
    <div class="actions"><button id="closeBtn" type="button">关闭此页面</button></div>
    <div class="hint"><span id="hintText">{{HINT}}</span></div>
    <div class="brand">Class Daily Land 2 · 插件市场</div>
  </main>
  <script>
  (function () {
    var card = document.getElementById('card');
    var hintText = document.getElementById('hintText');
    var DURATION = {{DURATION_MS}};
    var closing = false;

    function attemptClose() {
      try { window.open('', '_self'); } catch (e) {}
      try { window.close(); } catch (e) {}
    }

    // 关闭统一走这里：先淡出（视觉上有收束感），再真正关；
    // 若浏览器拦下了（页面还活着），把页面恢复出来并提示手动关闭，
    // 否则用户会看到一张全透明的空白页，以为卡死。
    function beginClose() {
      if (closing) return;
      closing = true;
      card.classList.add('closing');
      setTimeout(attemptClose, 260);
      setTimeout(function () {
        if (document.hidden) return;
        card.classList.remove('closing');
        hintText.textContent = '浏览器阻止了自动关闭：按 Ctrl+W，或点击上面的按钮即可。';
        var ring = document.querySelector('.ring');
        if (ring) ring.style.opacity = '0';
      }, 900);
    }

    document.getElementById('closeBtn').addEventListener('click', beginClose);
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
            .Replace("{{RING}}", ring)
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title))
            .Replace("{{MESSAGE}}", System.Net.WebUtility.HtmlEncode(message))
            .Replace("{{HINT}}", System.Net.WebUtility.HtmlEncode(hint))
            .Replace("{{DURATION_S}}", "3.2")
            .Replace("{{DURATION_MS}}", "3200")
            .Replace("{{AUTO_CLOSE}}", autoClose);
    }
}

/// <summary>浏览器登录不可用（端口占用 / 浏览器打不开）：调用方据此退回内嵌 WebView2。</summary>
internal sealed class BrowserLoginUnavailableException : Exception
{
    public BrowserLoginUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
