using System.Text.Json.Serialization;

namespace ClassDailyLand2.UpdateContract;

/// <summary>
/// 远端 <c>version.json</c> 的 DTO。
///
/// 字段名与远端 JSON 严格一致（snake_case），由 <see cref="JsonPropertyNameAttribute"/> 固定 ——
/// 这份文件是**别人手写在 git 仓库里的**，不能指望它跟 C# 命名习惯对齐。
///
/// 容错原则：**任何一个通道坏了都不能让整个文件解析失败**。
/// 具体做法是逐块校验（版本号能否解析、url 是否 http(s)、sha256 是否 64 位十六进制），
/// 不合格的通道标记 <c>IsUsable = false</c> 并给出人话原因，其它通道照常可用。
/// </summary>
public sealed class RemoteVersionFile
{
    [JsonPropertyName("stable")] public RemoteChannel? Stable { get; set; }
    [JsonPropertyName("beta")] public RemoteChannel? Beta { get; set; }

    /// <summary>资源包块。远端可能不写整块，也可能不写 <c>is_mandatory</c>（本块没有这个概念）。</summary>
    [JsonPropertyName("resources")] public RemoteResourceChannel? Resources { get; set; }
}

/// <summary>一个程序包通道（stable / beta）。</summary>
public sealed class RemoteChannel
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("release_date")] public string ReleaseDate { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>更新说明。远端也可能写成单个字符串（手改事故），用弹性转换器接住。</summary>
    [JsonPropertyName("changelog")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> Changelog { get; set; } = new();

    [JsonPropertyName("is_mandatory")] public bool IsMandatory { get; set; }

    // ——— 以下为解析后填充的运行时状态，不参与序列化 ———

    /// <summary>这一块能不能用（版本可解析 + url 合法 + sha256 格式正确）。</summary>
    [JsonIgnore] public bool IsUsable { get; set; } = true;

    /// <summary>不可用的原因（人话），可用时为 null。</summary>
    [JsonIgnore] public string? RejectReason { get; set; }

    [JsonIgnore] public SemVer VersionValue => SemVer.ParseOrZero(Version);
}

/// <summary>
/// 资源包通道。与 <see cref="RemoteChannel"/> 的唯一区别：
/// **没有 is_mandatory**（用户给的格式里 resources 块就没这个字段）。
/// 这里仍留一个可空属性：万一远端多写了一个，也别崩。
/// </summary>
public sealed class RemoteResourceChannel
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("release_date")] public string ReleaseDate { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    [JsonPropertyName("changelog")]
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string> Changelog { get; set; } = new();

    [JsonPropertyName("is_mandatory")] public bool? IsMandatory { get; set; }

    [JsonIgnore] public bool IsUsable { get; set; } = true;
    [JsonIgnore] public string? RejectReason { get; set; }
    [JsonIgnore] public SemVer VersionValue => SemVer.ParseOrZero(Version);
}

/// <summary>
/// 把 <c>["a","b"]</c> / <c>"a"</c> / <c>[{"text":"a"}]</c> 都读成 <c>List&lt;string&gt;</c>。
/// 远端文件是手写的，写成单个字符串很常见，为这个崩掉整次检查不值得。
/// </summary>
public sealed class FlexibleStringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
                                       System.Text.Json.JsonSerializerOptions options)
    {
        var result = new List<string>();

        // 单个字符串：changelog: "只有这一条"
        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        {
            Add(result, reader.GetString());
            return result;
        }

        if (reader.TokenType == System.Text.Json.JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
            {
                switch (reader.TokenType)
                {
                    case System.Text.Json.JsonTokenType.String:
                        Add(result, reader.GetString());
                        break;

                    case System.Text.Json.JsonTokenType.StartObject:
                        // {"text":"..."} / {"message":"..."} 之类：取对象里第一个字符串**值**
                        // ★ 注意 PropertyName 不是 String token，Skip() 会连它的值一起吃掉，
                        //   所以这里显式跳过属性名、只认值。
                        while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndObject)
                        {
                            if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
                                continue;                                  // 只是属性名，下一轮读值
                            if (reader.TokenType == System.Text.Json.JsonTokenType.String)
                                Add(result, reader.GetString());
                        }
                        break;

                    default:
                        // 数字 / null / true 之类的元素直接忽略。
                        // ★ 这里绝不能 reader.Skip()：Skip 会把当前位置之后的兄弟节点一起跳过。
                        break;
                }
            }
            return result;
        }

        // 其它形状（对象、数字……）直接忽略，不要抛
        return result;
    }

    static void Add(List<string> list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var text = value.Trim();
        if (text.Length > 0) list.Add(text);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, List<string> value,
                               System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
