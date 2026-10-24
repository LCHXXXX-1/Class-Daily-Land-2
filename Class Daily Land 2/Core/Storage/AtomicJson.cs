using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassDailyLand.Core.Storage;

/// <summary>
/// 原子 JSON 读写：写临时文件 → File.Replace（不存在则 File.Move overwrite）。
/// 绝不直接覆盖目标文件，进程崩溃 / 断电都不会留下半截 JSON。
/// </summary>
public static class AtomicJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同一文件的写操作串行化（进程内），跨进程靠 File.Replace 的原子性。</summary>
    static SemaphoreSlim LockFor(string path) =>
        Locks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

    public static void Write(string path, object value)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = full + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Options);

        var gate = LockFor(full);
        gate.Wait();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(tmp)) TryDelete(tmp);
                    File.WriteAllBytes(tmp, json);
                    FlushToDisk(tmp);

                    if (File.Exists(full))
                    {
                        // 目标存在：File.Replace 原子替换（保留备份参数为 null）
                        try
                        {
                            File.Replace(tmp, full, null);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            File.Move(tmp, full, overwrite: true);
                        }
                    }
                    else
                    {
                        File.Move(tmp, full, overwrite: true);
                    }
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(40 * attempt);   // 偶发占用（杀软/索引）重试
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    Thread.Sleep(40 * attempt);
                }
            }
        }
        finally
        {
            TryDelete(tmp);
            gate.Release();
        }
    }

    static void FlushToDisk(string tmp)
    {
        try
        {
            using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Flush(true);
        }
        catch (IOException) { /* 刷盘失败不影响替换语义 */ }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 忽略 */ }
    }

    /// <summary>读并反序列化；文件不存在或损坏时返回 default 并把坏文件改名备份。</summary>
    public static T Read<T>(string path, Func<T> factory, bool quarantineBad = true)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return factory();
        try
        {
            var text = File.ReadAllText(full, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return factory();
            var node = JsonNode.Parse(text);
            if (node is null) return factory();
            return node.Deserialize<T>(Options) ?? factory();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            if (quarantineBad)
            {
                try { File.Move(full, full + ".bad", overwrite: true); } catch { }
            }
            return factory();
        }
    }

    /// <summary>读 settings.json 这类需要逐键校验的扁平对象；返回原始键值（JsonElement 已降级）。</summary>
    public static Dictionary<string, object?> ReadFlat(string path)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return result;
        try
        {
            var text = File.ReadAllText(full, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return result;
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null) return result;
            foreach (var kv in node)
                result[kv.Key] = Unwrap(kv.Value);
        }
        catch (JsonException)
        {
            try { File.Move(full, full + ".bad", overwrite: true); } catch { }
        }
        return result;
    }

    static object? Unwrap(JsonNode? node)
    {
        if (node is null) return null;
        var v = node.GetValueKind();
        switch (v)
        {
            case JsonValueKind.True: return node.GetValue<bool>();
            case JsonValueKind.False: return node.GetValue<bool>();
            case JsonValueKind.Number:
                var d = node.GetValue<double>();
                if (d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
                return d;
            case JsonValueKind.String: return node.GetValue<string>();
            case JsonValueKind.Array: return node.AsArray().Select(Unwrap).ToList();
            case JsonValueKind.Object:
                return node.AsObject().ToDictionary(p => p.Key, p => Unwrap(p.Value), StringComparer.Ordinal);
            default: return node.ToJsonString();
        }
    }

    public static void WriteJsonDoc(string path, JsonObject obj) => Write(path, obj);
}
