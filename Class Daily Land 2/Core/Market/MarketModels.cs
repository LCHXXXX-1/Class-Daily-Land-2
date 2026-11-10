using ClassDailyLand2.UpdateContract;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClassDailyLand2.Core.Market;

/// <summary>
/// 插件市场索引（<c>plugins/index.json</c>）的数据模型。
///
/// <para><b>一个文件装全部插件</b>：客户端只请求这一个 JSON，里面的 <see cref="Plugins"/>
/// 数组可以装任意多个插件；仓库里按插件拆分的 <c>entries/&lt;id&gt;.json</c> 只是编辑单元，
/// 由 <c>tools/market.mjs build</c> 聚合进来。</para>
///
/// <para><b>解析纪律</b>（与 <c>PluginCatalog</c> 的哲学一致）：单条坏数据只丢这一条，
/// 并记下原因；只有 <c>schema</c> 比自己支持的更新时，才整份拒用 —— 那是"看不懂"，
/// 不是"这一条有问题"。</para>
/// </summary>
public sealed class MarketIndex
{
    /// <summary>本程序能理解的索引格式版本。</summary>
    public const int SupportedSchema = 1;

    [JsonPropertyName("schema")] public int Schema { get; init; } = SupportedSchema;

    [JsonPropertyName("generated_at")] public string GeneratedAt { get; init; } = "";

    [JsonPropertyName("count")] public int Count { get; init; }

    [JsonPropertyName("plugins")] public List<MarketEntry> Plugins { get; init; } = new();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 解析并逐条校验。返回的 <c>Index</c> 里只保留合法条目，
    /// <c>Warnings</c> 说明被丢掉的那些为什么被丢。
    /// </summary>
    public static MarketParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new MarketParseResult(null, Array.Empty<string>(), "索引内容为空。");

        MarketIndex? raw;
        try
        {
            raw = JsonSerializer.Deserialize<MarketIndex>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new MarketParseResult(null, Array.Empty<string>(), "索引不是合法 JSON：" + ex.Message);
        }

        if (raw is null)
            return new MarketParseResult(null, Array.Empty<string>(), "索引解析结果为空。");

        if (raw.Schema > SupportedSchema)
        {
            return new MarketParseResult(null, Array.Empty<string>(),
                $"索引格式 v{raw.Schema} 比本程序支持的 v{SupportedSchema} 新，请升级主程序。");
        }

        var warnings = new List<string>();
        var kept = new List<MarketEntry>(raw.Plugins.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in raw.Plugins)
        {
            var problem = entry.Validate();
            if (problem is not null)
            {
                warnings.Add($"{Name(entry)}：{problem}");
                continue;
            }

            if (!seen.Add(entry.Id))
            {
                // 撞 id 时保留版本更大的那个（手工改索引最容易出这种错）
                var existing = kept.First(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                if (entry.VersionValue > existing.VersionValue)
                {
                    kept.Remove(existing);
                    kept.Add(entry);
                    warnings.Add($"{entry.Id}：索引里出现重复 id，已保留版本更高的 {entry.Version}");
                }
                else
                {
                    warnings.Add($"{entry.Id}：索引里出现重复 id，已忽略版本 {entry.Version}");
                }
                continue;
            }

            kept.Add(entry);
        }

        var index = new MarketIndex
        {
            Schema = raw.Schema,
            GeneratedAt = raw.GeneratedAt,
            Count = kept.Count,
            Plugins = kept,
        };
        return new MarketParseResult(index, warnings, null);
    }

    static string Name(MarketEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Id) ? "(没有 id 的条目)" : entry.Id;
}

/// <summary>索引里的一个插件，对应远端 <c>entries/&lt;id&gt;.json</c> 聚合成的那条记录。</summary>
public sealed partial class MarketEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("api_version")] public int ApiVersion { get; init; } = 1;
    [JsonPropertyName("min_app_version")] public string? MinAppVersion { get; init; }
    [JsonPropertyName("target_sdk_version")] public string? TargetSdkVersion { get; init; }
    [JsonPropertyName("category")] public string Category { get; init; } = "other";
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
    [JsonPropertyName("trust")] public string Trust { get; init; } = "community";
    [JsonPropertyName("icon")] public string? Icon { get; init; }

    /// <summary>安装包路径：相对 <c>plugins/</c> 的路径，或 http(s) 绝对地址（大包走 Releases）。</summary>
    [JsonPropertyName("path")] public string Path { get; init; } = "";

    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("homepage")] public string? Homepage { get; init; }
    [JsonPropertyName("repo")] public string? Repo { get; init; }
    [JsonPropertyName("license")] public string? License { get; init; }
    [JsonPropertyName("published_at")] public string PublishedAt { get; init; } = "";
    [JsonPropertyName("changelog")] public List<string>? Changelog { get; init; }
    [JsonPropertyName("revoked")] public bool Revoked { get; init; }

    /// <summary>已解析的版本号；解析失败时为 0.0.0。</summary>
    [JsonIgnore] public SemVer VersionValue => SemVer.ParseOrZero(Version);

    /// <summary>哈希是否可用 —— 不可用的条目一律不许安装（同更新器对 sha256 的判法）。</summary>
    [JsonIgnore]
    public bool HasValidHash =>
        Sha256.Length == 64 && Sha256.All(Uri.IsHexDigit);

    /// <summary>包地址是绝对 URL（Releases 逃生口）而不是相对路径。</summary>
    [JsonIgnore]
    public bool HasAbsolutePackage =>
        Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>分类：未知值归 other（前后兼容，别让新分类把老客户端搞崩）。</summary>
    [JsonIgnore]
    public string CategoryOrDefault =>
        Categories.Contains(Category) ? Category : "other";

    /// <summary>信任级别：未知值按 community（宁可多提示一句）。</summary>
    [JsonIgnore]
    public string TrustOrDefault =>
        Trust is "official" or "verified" or "community" ? Trust : "community";

    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "timetable", "weather", "reminder", "tools", "media", "theme", "data", "other",
    };

    /// <summary>校验一个条目；返回 null 表示没问题，否则返回人话原因。</summary>
    public string? Validate()
    {
        if (!IdPattern().IsMatch(Id)) return $"id「{Id}」不合法";
        if (string.IsNullOrWhiteSpace(Name)) return "缺少 name";
        if (string.IsNullOrWhiteSpace(Summary)) return "缺少 summary";
        if (string.IsNullOrWhiteSpace(Author)) return "缺少 author";
        if (!SemVer.TryParse(Version, out _)) return $"version「{Version}」不是合法版本号";
        if (!HasValidHash) return "sha256 缺失或格式不对，不能安装";
        if (string.IsNullOrWhiteSpace(Path)) return "缺少 path";
        if (!HasAbsolutePackage && (Path.Contains("..") || Path.Contains('\\')))
            return $"path「{Path}」不合法";
        if (ApiVersion <= 0) return "api_version 不合法";
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();
}

/// <summary>索引解析结果：<see cref="Index"/> 为 null 表示整份不可用。</summary>
public sealed record MarketParseResult(
    MarketIndex? Index,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public bool Ok => Index is not null && Error is null;
}
