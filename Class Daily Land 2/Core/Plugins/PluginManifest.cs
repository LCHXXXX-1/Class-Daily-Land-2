using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ClassDailyLand2.Core.Plugins;

/// <summary>
/// 插件清单（<c>plugins/&lt;目录&gt;/plugin.json</c>）。
///
/// 为什么元数据放清单而不是代码：**装了但加载失败的插件也要能列在设置中心**。
/// 反射失败时连类型都拿不到，只有清单能告诉我们它叫什么、该是什么版本。
/// </summary>
public sealed partial class PluginManifest
{
    /// <summary>宿主实现的契约大版本。清单里 apiVersion 大于它的插件拒绝加载。</summary>
    public const int HostApiVersion = 1;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";

    /// <summary>入口程序集文件名（只允许文件名，不允许路径）。</summary>
    public string Entry { get; init; } = "";

    /// <summary>实现 <c>IPlugin</c> 的完整类型名。</summary>
    public string Type { get; init; } = "";

    /// <summary>插件声明的契约版本，默认 1。</summary>
    public int ApiVersion { get; init; } = HostApiVersion;

    /// <summary>插件编译时依赖的 SDK（ClassDailyLand2.Plugin.Abstractions）版本；空表示不校验。</summary>
    public string? TargetSdkVersion { get; init; }

    /// <summary>插件要求的最低主程序版本；空表示不校验。</summary>
    public string? MinAppVersion { get; init; }

    public string? Homepage { get; init; }

    /// <summary>插件目录的绝对路径（不来自清单，扫描时填）。</summary>
    public string Directory { get; init; } = "";

    public string EntryPath => string.IsNullOrEmpty(Entry) ? "" : Path.Combine(Directory, Entry);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // 目录名 = id 时最不容易出岔子：卸载靠 id 定位目录。允许点与横线，禁止路径分隔符。
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    /// <summary>从 JSON 文本解析并校验。失败时 <paramref name="error"/> 给出人话原因。</summary>
    public static bool TryParse(string json, string directory, out PluginManifest manifest, out string? error)
    {
        manifest = new PluginManifest();
        error = null;

        JsonDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<JsonDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = "plugin.json 不是合法 JSON：" + ex.Message;
            return false;
        }

        if (dto is null)
        {
            error = "plugin.json 内容为空。";
            return false;
        }

        var id = (dto.Id ?? "").Trim();
        if (id.Length == 0) { error = "缺少 id。"; return false; }
        if (!IdPattern().IsMatch(id))
        {
            error = $"id「{id}」不合法：只能用字母数字与 . _ -，长度 1~64。";
            return false;
        }

        var entry = (dto.Entry ?? "").Trim();
        if (entry.Length == 0) { error = "缺少 entry（入口 dll 文件名）。"; return false; }
        if (entry.Contains('/') || entry.Contains('\\') || entry.Contains(".."))
        {
            error = $"entry「{entry}」不合法：只能是插件目录下的文件名。";
            return false;
        }
        if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = $"entry「{entry}」必须是 .dll。";
            return false;
        }

        var type = (dto.Type ?? "").Trim();
        if (type.Length == 0) { error = "缺少 type（实现 IPlugin 的完整类型名）。"; return false; }

        var api = dto.ApiVersion is > 0 ? dto.ApiVersion.Value : HostApiVersion;
        if (api > HostApiVersion)
        {
            error = $"插件需要契约 v{api}，本版本只支持到 v{HostApiVersion}。请升级宿主或换旧版插件。";
            return false;
        }

        manifest = new PluginManifest
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name!.Trim(),
            Version = string.IsNullOrWhiteSpace(dto.Version) ? "1.0.0" : dto.Version!.Trim(),
            Description = (dto.Description ?? "").Trim(),
            Author = (dto.Author ?? "").Trim(),
            Entry = entry,
            Type = type,
            ApiVersion = api,
            TargetSdkVersion = string.IsNullOrWhiteSpace(dto.TargetSdkVersion) ? null : dto.TargetSdkVersion!.Trim(),
            MinAppVersion = string.IsNullOrWhiteSpace(dto.MinAppVersion) ? null : dto.MinAppVersion!.Trim(),
            Homepage = string.IsNullOrWhiteSpace(dto.Homepage) ? null : dto.Homepage!.Trim(),
            Directory = directory,
        };
        return true;
    }

    /// <summary>读一个目录下的 plugin.json。文件不存在也算失败（带明确原因）。</summary>
    public static bool TryLoad(string directory, out PluginManifest manifest, out string? error)
    {
        manifest = new PluginManifest();
        var path = Path.Combine(directory, "plugin.json");
        if (!File.Exists(path))
        {
            error = "缺少 plugin.json。";
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "读不了 plugin.json：" + ex.Message;
            return false;
        }

        if (!TryParse(json, directory, out manifest, out error)) return false;

        if (!File.Exists(manifest.EntryPath))
        {
            error = $"找不到入口程序集 {manifest.Entry}。";
            return false;
        }
        return true;
    }

    /// <summary>序列化回 JSON（安装时校验过重新写回用，保证格式统一）。</summary>
    public string ToJson() => JsonSerializer.Serialize(new JsonDto
    {
        Id = Id,
        Name = Name,
        Version = Version,
        Description = Description,
        Author = Author,
        Entry = Entry,
        Type = Type,
        ApiVersion = ApiVersion,
        TargetSdkVersion = TargetSdkVersion,
        MinAppVersion = MinAppVersion,
        Homepage = Homepage,
    }, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    });

    sealed class JsonDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? Entry { get; set; }
        public string? Type { get; set; }
        public int? ApiVersion { get; set; }

        [JsonPropertyName("target_sdk_version")]
        public string? TargetSdkVersion { get; set; }

        [JsonPropertyName("min_app_version")]
        public string? MinAppVersion { get; set; }

        public string? Homepage { get; set; }
    }
}
