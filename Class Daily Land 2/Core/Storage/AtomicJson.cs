using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassDailyLand2.Core.Storage;

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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Logger.Current?.Trace($"[文件写] 开始 {full}（类型 {value.GetType().Name}，序列化 {json.Length} 字节）");

        var gate = LockFor(full);
        gate.Wait();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (attempt > 1)
                        Logger.Current?.Trace($"[文件写] 第 {attempt} 次重试 {full}（前次被占用，已退避 {40 * (attempt - 1)}ms）");
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
                    Logger.Current?.Trace($"[文件写] 完成 {full}（{json.Length} 字节，{sw.ElapsedMilliseconds}ms，尝试 {attempt} 次）");
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
        catch (Exception ex)
        {
            // 写盘彻底失败：一定要留一条 ERROR（调用方可能只是静默 catch 掉）
            Logger.Current?.Error($"[文件写] 失败 {full}（{ex.GetType().Name}: {ex.Message}，已重试 5 次）");
            throw;
        }
        finally
        {
            TryDelete(tmp);
            gate.Release();
        }
    }

    /// <summary>
    /// 把 tmp 文件的系统缓存刷到磁盘。
    ///
    /// ★ 必须以**可写**句柄打开：FileStream.Flush(true) 内部是
    ///   <c>if (flushToDisk &amp;&amp; CanWrite) FlushFileBuffers()</c>，
    ///   用 FileAccess.Read 打开时 CanWrite=false，这一句等于什么都没做 ——
    ///   断电时数据还在系统缓存里，替换完就是一份空白文件。
    /// </summary>
    static void FlushToDisk(string tmp)
    {
        try
        {
            using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.Flush(true);
        }
        catch (IOException) { /* 刷盘失败不影响替换语义，但不能因此不写盘 */ }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 忽略 */ }
    }

    /// <summary>读并反序列化；文件不存在或损坏时返回 default 并把坏文件改名备份。</summary>
    public static T Read<T>(string path, Func<T> factory, bool quarantineBad = true)
    {
        var full = Path.GetFullPath(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!File.Exists(full))
        {
            Logger.Current?.Info($"[文件读] 缺失 {full}，使用默认 {typeof(T).Name}");
            return factory();
        }
        try
        {
            var text = File.ReadAllText(full, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                Logger.Current?.Trace($"[文件读] {full} 为空白，使用默认 {typeof(T).Name}");
                return factory();
            }
            var node = JsonNode.Parse(text);
            if (node is null)
            {
                Logger.Current?.Trace($"[文件读] {full} 解析为 null，使用默认 {typeof(T).Name}");
                return factory();
            }
            var result = node.Deserialize<T>(Options) ?? factory();
            Logger.Current?.Trace($"[文件读] 完成 {full}（{text.Length} 字符 → {typeof(T).Name}，{sw.ElapsedMilliseconds}ms）");
            return result;
        }
        catch (JsonException ex)
        {
            // 真的是坏内容：隔离 + 回默认值（这是 quarantine 这个机制存在的唯一理由）
            Logger.Current?.Warn($"[文件读] {full} 内容损坏（{ex.Message}），回退默认值" +
                                 (quarantineBad ? "，原文件隔离为 .bad" : ""));
            if (quarantineBad)
            {
                try { File.Move(full, full + ".bad", overwrite: true); } catch { }
            }
            return factory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ★ 读不动 ≠ 文件坏了。杀软扫描、同步盘临时占用都会抛 IOException，
            //   原来的写法会把好好的 schedule.json 改名 .bad，接着任意一次 Save 就用默认数据占了原名字 ——
            //   用户看到的就是"课表凭空变回默认"。这里只重试、不隔离。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Thread.Sleep(60);
                    var retryText = File.ReadAllText(full, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(retryText)) continue;
                    var retryNode = JsonNode.Parse(retryText);
                    if (retryNode is not null)
                    {
                        var retryValue = retryNode.Deserialize<T>(Options);
                        if (retryValue is not null) return retryValue;
                    }
                }
                catch { /* 继续重试 */ }
            }

            Logger.Current?.Error($"[文件读] {full} 暂时读不出来（{ex.GetType().Name}: {ex.Message}），" +
                                  "本次用默认值但不隔离原文件");
            return factory();
        }
    }

    /// <summary>读 settings.json 这类需要逐键校验的扁平对象；返回原始键值（JsonElement 已降级）。</summary>
    public static Dictionary<string, object?> ReadFlat(string path)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var full = Path.GetFullPath(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!File.Exists(full))
        {
            Logger.Current?.Info($"[文件读] 缺失 {full}，返回空键值集");
            return result;
        }
        try
        {
            var text = File.ReadAllText(full, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                Logger.Current?.Trace($"[文件读] {full} 为空白，返回空键值集");
                return result;
            }
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null)
            {
                Logger.Current?.Trace($"[文件读] {full} 不是 JSON 对象，返回空键值集");
                return result;
            }
            foreach (var kv in node)
                result[kv.Key] = Unwrap(kv.Value);
            Logger.Current?.Trace($"[文件读] 完成 {full}（{text.Length} 字符 → {result.Count} 个键，{sw.ElapsedMilliseconds}ms）");
        }
        catch (JsonException ex)
        {
            Logger.Current?.Warn($"[文件读] {full} JSON 解析失败（{ex.Message}），原文件隔离为 .bad");
            try { File.Move(full, full + ".bad", overwrite: true); } catch { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 设置文件是启动链路上第一份数据：这里让异常冒出去 = 整个程序起不来。
            // 读不动就按"没有设置"处理，下次保存会重新写一份完好的。
            Logger.Current?.Error($"[文件读] {full} 读不出来（{ex.GetType().Name}: {ex.Message}），本次按空设置处理");
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
