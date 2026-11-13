using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiquidGlass.Rendering;

/// <summary>
/// 一帧背景快照：低分辨率位图 + 它在 DIP 坐标系里的覆盖范围。
/// </summary>
/// <remarks>
/// 多个玻璃控件共用同一份快照（要求里的「共享背景采样资源」）：
/// 每个控件只是用不同的 Viewbox 从这张图上裁自己那一块，
/// 而不是各自去截一次屏 —— 后者在 10 个控件时会把帧率直接砍半。
/// </remarks>
public sealed class BackdropSnapshot
{
    /// <summary>降采样后的背景位图（已冻结，可跨线程读）。</summary>
    public required BitmapSource Image { get; init; }

    /// <summary>这张图对应的 DIP 范围（相对采样根元素）。</summary>
    public required Rect Bounds { get; init; }

    /// <summary>图像像素 / DIP 的比例（= 1 / 降采样倍数）。</summary>
    public required double Scale { get; init; }

    /// <summary>版本号：内容或尺寸任何变化都会 +1，缓存据此失效。</summary>
    public required long Version { get; init; }

    /// <summary>估算的字节占用（缓存统计用）。</summary>
    public long ByteSize => (long)Image.PixelWidth * Image.PixelHeight * 4;

    /// <summary>
    /// 把控件在 DIP 里的矩形换算成这张图上的相对 Viewbox（0~1）。
    /// 用于 ImageBrush.Viewbox —— 这样每个控件用自己的裁切窗口共享同一张位图。
    /// </summary>
    public Rect ToRelativeViewbox(Rect controlRect)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return new Rect(0, 0, 1, 1);

        // 采样区域比控件小的时候（控件露出采样根之外）要夹住，否则 Viewbox 会出界变黑边
        var x = (controlRect.X - Bounds.X) / Bounds.Width;
        var y = (controlRect.Y - Bounds.Y) / Bounds.Height;
        var w = controlRect.Width / Bounds.Width;
        var h = controlRect.Height / Bounds.Height;

        return new Rect(
            Math.Clamp(x, 0, 1),
            Math.Clamp(y, 0, 1),
            Math.Clamp(w, 1e-4, 1),
            Math.Clamp(h, 1e-4, 1));
    }
}

/// <summary>
/// 背景快照缓存：按「采样根 + 缩放比」保存最近一次快照，并在失效时重建。
/// </summary>
public sealed class BackdropCache : IDisposable
{
    readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    long _version;
    long _bytes;

    /// <summary>缓存命中/未命中计数（性能面板用）。</summary>
    public long Hits { get; private set; }

    /// <summary>未命中次数。</summary>
    public long Misses { get; private set; }

    /// <summary>当前缓存占用字节。</summary>
    public long Bytes => _bytes;

    sealed class CacheEntry
    {
        public required BackdropSnapshot Snapshot { get; init; }
        public required DateTime Stamp { get; set; }
    }

    /// <summary>
    /// 取（或创建）一份快照。工厂只在未命中时调用。
    /// </summary>
    /// <param name="key">缓存键，通常是「根元素标识 + 尺寸 + 缩放比」。</param>
    /// <param name="scale">降采样比。</param>
    /// <param name="bounds">要覆盖的 DIP 范围。</param>
    /// <param name="factory">真正去截屏/渲染的回调。</param>
    public BackdropSnapshot GetOrCreate(string key, double scale, Rect bounds, Func<double, Rect, BitmapSource> factory)
    {
        if (_entries.TryGetValue(key, out var hit)
            && Math.Abs(hit.Snapshot.Scale - scale) < 1e-6
            && hit.Snapshot.Bounds == bounds)
        {
            Hits++;
            hit.Stamp = DateTime.UtcNow;
            return hit.Snapshot;
        }

        Misses++;
        var bitmap = factory(scale, bounds);
        if (bitmap.CanFreeze) bitmap.Freeze();

        var snapshot = new BackdropSnapshot
        {
            Image = bitmap,
            Bounds = bounds,
            Scale = scale,
            Version = ++_version,
        };

        if (_entries.TryGetValue(key, out var old)) _bytes -= old.Snapshot.ByteSize;
        _entries[key] = new CacheEntry { Snapshot = snapshot, Stamp = DateTime.UtcNow };
        _bytes += snapshot.ByteSize;
        return snapshot;
    }

    /// <summary>让所有缓存失效（背景动画、窗口缩放、主题切换时调用）。</summary>
    public void Invalidate() => _entries.Clear();

    /// <summary>只让某个键失效。</summary>
    public void Invalidate(string key)
    {
        if (_entries.Remove(key, out var old)) _bytes -= old.Snapshot.ByteSize;
    }

    public void Dispose()
    {
        _entries.Clear();
        _bytes = 0;
    }
}
