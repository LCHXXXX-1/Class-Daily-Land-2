using ClassDailyLand2.Core.Storage;

namespace ClassDailyLand2.Core.Market;

/// <summary>缓存文件（<c>settings/market-cache.json</c>）的形状。</summary>
public sealed class MarketCacheFile
{
    public MarketIndex? Index { get; set; }
    public string Source { get; set; } = "";
    public DateTime FetchedAt { get; set; }
    public string GeneratedAt { get; set; } = "";
}

/// <summary>
/// 索引缓存。两个作用：
/// <list type="number">
///   <item>半天内重复打开插件页不再发请求（Gitee raw 再快也有 CDN 抖动）；</item>
///   <item>断网时还能把上次的市场列出来，并明确标注「数据来自 X 小时前」——
///         空列表 + 报错是最没用的失败态。</item>
/// </list>
/// </summary>
public static class MarketCache
{
    /// <summary>
    /// 多久算新鲜（超过就重新拉；未过期时直接用缓存，不发请求）。
    ///
    /// <para>10 分钟：作者刚发的新版本，用户最多十分钟就能在列表里看到。
    /// 之前是 12 小时 —— 结果是"我明明发了 1.1.0，他那边还显示 1.0.0"。</para>
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    /// <summary>只有拿到过索引才算缓存有用。</summary>
    public static MarketCacheFile? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var file = AtomicJson.Read(path, () => new MarketCacheFile());
        return file.Index is null || file.Index.Plugins.Count == 0 ? null : file;
    }

    public static void Save(string path, MarketFetch fetch)
    {
        if (fetch.Index is null) return;
        try
        {
            AtomicJson.Write(path, new MarketCacheFile
            {
                Index = fetch.Index,
                Source = fetch.Source,
                FetchedAt = fetch.FetchedAt,
                GeneratedAt = fetch.Index.GeneratedAt,
            });
        }
        catch (Exception)
        {
            // 缓存写不进去不影响本次使用：内存里已经有索引了
        }
    }

    public static bool IsFresh(MarketCacheFile? cache) =>
        cache?.Index is not null && DateTime.Now - cache.FetchedAt < FreshFor;

    /// <summary>把缓存包装成一次"拉取结果"，供界面统一处理。</summary>
    public static MarketFetch AsFetch(MarketCacheFile cache) =>
        new(cache.Index, cache.Source, "", Array.Empty<string>(), cache.FetchedAt);
}
