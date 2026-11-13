using System;
using System.Diagnostics;
using System.Windows.Media;

namespace LiquidGlass.Rendering;

/// <summary>
/// 性能监控：FPS、背景采样次数、活跃玻璃控件数、缓存占用。
/// Demo 的性能面板直接读这里。
/// </summary>
/// <remarks>
/// 用环形缓冲算平均帧率，而不是「上一帧到现在」的瞬时值 ——
/// WPF 是按需渲染的：鼠标不动时根本不出帧，瞬时读数会掉到 0，看起来像卡死。
/// </remarks>
public sealed class GlassPerformanceMonitor
{
    const int Window = 60;

    readonly double[] _frameTimes = new double[Window];
    int _cursor;
    int _count;
    long _lastTicks;
    Stopwatch? _clock;

    /// <summary>活跃玻璃控件数量。</summary>
    public int ActiveSurfaces { get; set; }

    /// <summary>累计背景采样次数。</summary>
    public long BackdropSamples { get; set; }

    /// <summary>缓存占用（MB）。</summary>
    public double CacheMegabytes { get; set; }

    /// <summary>GPU 渲染时间（毫秒）；WPF 后端量不到就是 NaN，UI 显示成「—」。</summary>
    public double GpuMilliseconds { get; set; } = double.NaN;

    /// <summary>最近算出的平均帧率。</summary>
    public double FramesPerSecond { get; private set; }

    /// <summary>最近一帧的耗时（毫秒）。</summary>
    public double FrameMilliseconds { get; private set; }

    /// <summary>挂到 CompositionTarget.Rendering 上开始采样（重复调用无副作用）。</summary>
    public void Start()
    {
        if (_clock is not null) return;
        _clock = Stopwatch.StartNew();
        _lastTicks = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>停止采样。窗口关闭时务必调用，否则 CompositionTarget 会一直持有本对象。</summary>
    public void Stop()
    {
        if (_clock is null) return;
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
        _clock = null;
        _lastTicks = 0;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        if (_clock is null) return;

        var now = _clock.ElapsedTicks;
        if (_lastTicks != 0 && now > _lastTicks)
        {
            var deltaMs = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;

            // 掉帧 / 切窗口会给出巨大的间隔，夹到 500ms，免得一次卡顿永久污染平均值
            deltaMs = Math.Min(deltaMs, 500);
            FrameMilliseconds = deltaMs;

            _frameTimes[_cursor] = deltaMs;
            _cursor = (_cursor + 1) % Window;
            if (_count < Window) _count++;

            var sum = 0.0;
            for (var i = 0; i < _count; i++) sum += _frameTimes[i];
            var avg = sum / _count;
            FramesPerSecond = avg > 1e-6 ? 1000.0 / avg : 0;
        }

        _lastTicks = now;
    }
}
