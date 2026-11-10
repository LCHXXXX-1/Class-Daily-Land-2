using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassDailyLand2.MarketContract;

// ============================================================================
//  插件市场后端客户端（Shared 源码链接：主程序与 DeveloperIDE 各编一份）。
//  风格对齐 Updates\UpdateSources.cs：静态 HttpClient + 每请求 linked CTS 超时，
//  HttpClient.Timeout 常驻无限，避免 TaskCanceledException 套 InnerException。
// ============================================================================

#region 异常

/// <summary>市场接口非 2xx 统一抛这个，<see cref="StatusCode"/> 保留原始 HTTP 码。</summary>
public class MarketApiException : Exception
{
    public int StatusCode { get; }

    public MarketApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public MarketApiException(int statusCode, string message, Exception innerException) : base(message, innerException) => StatusCode = statusCode;
}

/// <summary>
/// 后端全局拦截（API.md §全局拦截）：username_set=false 时除登录/改名外一律 403「请先设置用户名」。
/// UI 捕获这个类型后要强制引导走 PUT /api/users/me/username，不能只弹个错误框了事。
/// </summary>
public sealed class UsernameRequiredException : MarketApiException
{
    public UsernameRequiredException() : base(403, "请先设置用户名") { }
}

#endregion

#region DTO（字段名与 API.md 一一对应，只增不改）

public sealed class MarketLoginResult
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("need_set_username")] public bool NeedSetUsername { get; set; }
    [JsonPropertyName("user")] public MarketUser? User { get; set; }
}

/// <summary>POST /api/auth/bind 的返回：{message, user}（不换 Token，原 JWT 继续有效）。</summary>
public sealed class MarketBindResult
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("user")] public MarketUser? User { get; set; }
}

/// <summary>只带 message 的返回（审核通过 / 拒绝等）。</summary>
public sealed class MarketMessage
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class MarketUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("is_admin")] public bool IsAdmin { get; set; }
    // 后端 User 表原样序列化：null = 该平台还没绑定，绑定后是第三方 uid 字符串
    [JsonPropertyName("gitee_id")] public string? GiteeId { get; set; }
    [JsonPropertyName("github_id")] public string? GithubId { get; set; }
}

public sealed class MarketFileEntry
{
    // ★ 文档没写 id 的类型，暂按 long；真正接 DELETE /api/files/:id 时先对一下实际响应
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("package_name")] public string PackageName { get; set; } = "";
    [JsonPropertyName("plugin_name")] public string PluginName { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("uploader_id")] public long UploaderId { get; set; }
    [JsonPropertyName("dev_mode")] public string DevMode { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    // 驳回理由：status=rejected 时有值（后端只增字段，老服务端不返回时保持 null）
    [JsonPropertyName("reject_reason")] public string? RejectReason { get; set; }

    /// <summary>状态中文：pending 审核中 / approved 已发布 / rejected 未通过 / draft 草稿（已撤销审核）。</summary>
    public string StatusText => Status switch
    {
        "approved" => "已发布",
        "rejected" => "未通过",
        "pending" => "审核中",
        "draft" => "草稿",
        _ => Status,
    };

    public string SizeText => Size <= 0 ? "" : $"{Size / 1024.0:F1} KB";

    /// <summary>created_at（RFC3339）截成 yyyy-MM-dd HH:mm 展示。</summary>
    public string TimeText => string.IsNullOrEmpty(CreatedAt)
        ? ""
        : CreatedAt![..Math.Min(16, CreatedAt.Length)].Replace('T', ' ');
}

public sealed class MarketMyFiles
{
    [JsonPropertyName("owned")] public List<MarketFileEntry> Owned { get; set; } = [];
    [JsonPropertyName("collab")] public List<MarketFileEntry> Collab { get; set; } = [];
}

public sealed class MarketPlugin
{
    [JsonPropertyName("package_name")] public string PackageName { get; set; } = "";
    [JsonPropertyName("plugin_name")] public string PluginName { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
}

public sealed class MarketPluginList
{
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("plugins")] public List<MarketPlugin> Plugins { get; set; } = [];
}

#endregion

public sealed class MarketApiClient
{
    const string BaseUrl = "https://api.class-daily-land.de5.net";
    const string UserAgent = "ClassDailyLand2-Market/1.0";
    const long MaxUploadBytes = 64 * 1024 * 1024;   // 后端限制 64MB，客户端先挡一道

    static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);

    static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// JWT。内存持有不落盘（持久化由 UI 层决定）。登录成功后自动赋值，
    /// 之后所有需鉴权请求自动注入 Authorization: Bearer。
    /// </summary>
    public string? Token { get; set; }

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain;q=0.9");
        return client;
    }

    // ————————————————————— API —————————————————————

    /// <summary>
    /// POST /api/auth/login。成功后 Token 自动持有；<see cref="MarketLoginResult.NeedSetUsername"/>
    /// 为 true 时 UI 必须引导改名，否则后续接口全被全局拦截。
    /// </summary>
    public async Task<MarketLoginResult> LoginAsync(
        string code, string provider = "github", CancellationToken ct = default)
    {
        var result = await SendAsync<MarketLoginResult>(
            HttpMethod.Post, "/api/auth/login", body: new { provider, code },
            auth: false, ListTimeout, ct).ConfigureAwait(false);
        Token = result.Token;
        return result;
    }

    /// <summary>PUT /api/users/me/username。重名后端报 409，按 MarketApiException 抛出。</summary>
    public Task<MarketLoginResult> SetUsernameAsync(string username, CancellationToken ct = default) =>
        SendAsync<MarketLoginResult>(HttpMethod.Put, "/api/users/me/username",
            body: new { username }, auth: true, ListTimeout, ct);

    /// <summary>POST /api/auth/bind。把另一平台绑到当前账号（后端可能合并删号），返回更新后的用户。</summary>
    public Task<MarketBindResult> BindAsync(string provider, string code, CancellationToken ct = default) =>
        SendAsync<MarketBindResult>(HttpMethod.Post, "/api/auth/bind",
            body: new { provider, code }, auth: true, ListTimeout, ct);

    // ————————————— 管理员审核（后端另有 is_admin 中间件，客户端只负责带上 JWT） —————————————

    /// <summary>GET /api/admin/files/pending。待审核列表。</summary>
    public Task<List<MarketFileEntry>> GetPendingAsync(CancellationToken ct = default) =>
        SendAsync<List<MarketFileEntry>>(HttpMethod.Get, "/api/admin/files/pending",
            body: null, auth: true, ListTimeout, ct);

    /// <summary>POST /api/admin/files/{id}/approve。推 GitHub + 刷 list.json，返回 message。</summary>
    public async Task<string> ApproveFileAsync(long id, CancellationToken ct = default)
    {
        var result = await SendAsync<MarketMessage>(HttpMethod.Post, "/api/admin/files/" + id + "/approve",
            body: null, auth: true, ListTimeout, ct).ConfigureAwait(false);
        return result.Message;
    }

    /// <summary>POST /api/admin/files/{id}/reject。reason 写进 reject_reason（重传新版本时自动清空）。</summary>
    public async Task<string> RejectFileAsync(long id, string reason, CancellationToken ct = default)
    {
        var result = await SendAsync<MarketMessage>(HttpMethod.Post, "/api/admin/files/" + id + "/reject",
            body: new { reason }, auth: true, ListTimeout, ct).ConfigureAwait(false);
        return result.Message;
    }

    /// <summary>GET /api/users/me。用存的 Token 校验登录态、刷新用户名时用。</summary>
    public Task<MarketUser> GetMeAsync(CancellationToken ct = default) =>
        SendAsync<MarketUser>(HttpMethod.Get, "/api/users/me", body: null, auth: true, ListTimeout, ct);

    /// <summary>GET /api/files/my。返回自传（owned）与参与合作（collab）两组。</summary>
    public Task<MarketMyFiles> GetMyFilesAsync(CancellationToken ct = default) =>
        SendAsync<MarketMyFiles>(HttpMethod.Get, "/api/files/my", body: null, auth: true, ListTimeout, ct);

    /// <summary>
    /// POST /api/files/{id}/withdraw。撤销「审核中」的插件 → 状态退回草稿、作废待审记录。
    /// 只有 pending 能撤（后端 409）；draft 不占用版本号，改完重传会自动回到 pending。
    /// </summary>
    public async Task<string> WithdrawFileAsync(long id, CancellationToken ct = default)
    {
        var result = await SendAsync<MarketMessage>(HttpMethod.Post, "/api/files/" + id + "/withdraw",
            body: null, auth: true, ListTimeout, ct).ConfigureAwait(false);
        return result.Message;
    }

    /// <summary>
    /// POST /api/files/upload。multipart 字段名固定 file，包名由后端生成
    /// （{当前用户名}.{plugin.json.name}），重复版本后端报 409。
    /// </summary>
    public async Task<MarketFileEntry> UploadPluginAsync(string filePath, CancellationToken ct = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException("插件包不存在", filePath);
        if (info.Length > MaxUploadBytes)
            throw new MarketApiException(0, $"插件包超过 64MB 限制（{info.Length / 1024.0 / 1024.0:F1} MB）");

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/files/upload");
        ApplyAuth(request);

        // 不把整个文件读进内存：StreamContent 边读边发
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new("application/octet-stream");
        request.Content = new MultipartFormDataContent
        {
            { fileContent, "file", info.Name },
        };

        var (status, body) = await SendAsync(request, UploadTimeout, ct).ConfigureAwait(false);
        return ReadResult<MarketFileEntry>(status, body);
    }

    /// <summary>GET /api/plugins。公开接口，platform 取 gitee|github（降级用 list.json 走另一路）。</summary>
    public Task<MarketPluginList> GetPluginsAsync(string platform = "github", CancellationToken ct = default) =>
        SendAsync<MarketPluginList>(HttpMethod.Get,
            "/api/plugins?platform=" + Uri.EscapeDataString(platform),
            body: null, auth: false, ListTimeout, ct);

    // ————————————————————— 管道 —————————————————————

    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool auth,
        TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        if (auth) ApplyAuth(request);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, MarketJson.Options), Encoding.UTF8, "application/json");

        var (status, text) = await SendAsync(request, timeout, ct).ConfigureAwait(false);
        return ReadResult<T>(status, text);
    }

    async Task<(int Status, string Body)> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token)
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        return ((int)response.StatusCode, text);
    }

    void ApplyAuth(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(Token))
            throw new MarketApiException(0, "尚未登录插件市场");
        request.Headers.Authorization = new("Bearer", Token);
    }

    T ReadResult<T>(int status, string body)
    {
        // 全局拦截先行：只要是「请先设置用户名」就抛专用异常，UI 捕获后引导改名
        if (status == 403 && body.Contains("请先设置用户名", StringComparison.Ordinal))
            throw new UsernameRequiredException();

        if (status is < 200 or >= 300)
            throw new MarketApiException(status, TryReadMessage(body) ?? $"HTTP {status}");

        try
        {
            var result = JsonSerializer.Deserialize<T>(body, MarketJson.Options);
            return result ?? throw new MarketApiException(status, "响应内容为空");
        }
        catch (JsonException ex)
        {
            throw new MarketApiException(status, "响应不是合法 JSON（" + ex.Message + "）", ex);
        }
    }

    /// <summary>错误统一格式 {"message": "..."}，取不到就回落到 HTTP 码。</summary>
    static string? TryReadMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("message", out var m) &&
                   m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>市场接口专用 JSON 选项（请求体是匿名对象、响应字段带显式 JsonPropertyName）。</summary>
public static class MarketJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
