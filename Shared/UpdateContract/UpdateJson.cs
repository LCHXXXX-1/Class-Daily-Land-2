using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassDailyLand2.UpdateContract;

/// <summary>
/// 主程序与更新器共用的 JSON 配置。
///
/// 两边是**两个进程**，跨进程契约只有 job.json 与 version.json 两个文件，
/// 所以序列化选项必须完全一致 —— 放在这里由两边 <c>Compile Include</c>，
/// 不单独产出程序集（否则又多一份 dll 要跟着更新包走）。
/// </summary>
public static class UpdateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,   // 手改 json 时允许注释
        AllowTrailingCommas = true,                        // 手改 json 时允许尾逗号
        NumberHandling = JsonNumberHandling.AllowReadingFromString,  // "size": "123" 也认
        WriteIndented = true,
        // 中文路径不要变成 \u4E2D，出问题时日志能直接看
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读 JSON；文件不存在、内容损坏一律返回 null（不抛）。</summary>
    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>原子写：先写 .tmp → 落盘 → 替换。跨进程可见性靠的是"读者要么看到旧文件要么看到新文件"。</summary>
    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(value, Options);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(json);
            writer.Flush();
            fs.Flush(true);
        }

        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
