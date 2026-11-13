using System;

namespace LiquidGlass.Core;

/// <summary>后端能力描述，供 UI 决定是否降级。</summary>
public sealed class GlassBackendCapabilities
{
    /// <summary>后端名称，如 "WPF ShaderEffect"、"DirectX 11"。</summary>
    public required string Name { get; init; }

    /// <summary>是否支持逐像素折射（真折射，而非模糊冒充）。</summary>
    public bool SupportsRefraction { get; init; }

    /// <summary>是否支持色散（RGB 通道分离采样）。</summary>
    public bool SupportsDispersion { get; init; }

    /// <summary>是否支持多控件共享背景纹理。</summary>
    public bool SupportsSharedBackdrop { get; init; }

    /// <summary>最高可用像素着色器版本（2 / 3 / 5 ...）。0 表示未知。</summary>
    public int MaxPixelShaderModel { get; init; }

    /// <summary>是否走 GPU 硬件渲染；false 表示软件降级。</summary>
    public bool IsHardwareAccelerated { get; init; }

    /// <summary>一段可直接显示给用户的能力说明。</summary>
    public string Describe()
        => $"{Name} · PS{MaxPixelShaderModel} · {(IsHardwareAccelerated ? "GPU" : "软件")}"
           + (SupportsRefraction ? " · 折射" : "")
           + (SupportsDispersion ? " · 色散" : "")
           + (SupportsSharedBackdrop ? " · 共享背景" : "");
}

/// <summary>
/// 渲染后端契约。WPF 后端与 DirectX 后端各自实现它，
/// 上层控件只依赖这个接口，切换后端不需要改控件代码。
/// </summary>
public interface IGlassRenderBackend : IDisposable
{
    /// <summary>本后端的名称（唯一标识，如 "wpf" / "dx11"）。</summary>
    string Id { get; }

    /// <summary>后端能力。</summary>
    GlassBackendCapabilities Capabilities { get; }

    /// <summary>标记某个区域需要重新采样背景。渲染层可据此做脏区域合并。</summary>
    void Invalidate(int x, int y, int width, int height);

    /// <summary>本帧是否需要重新采样（供调度器判断）。</summary>
    bool NeedsBackdropUpdate { get; }

    /// <summary>取当前帧的背景采样次数（性能面板用）。</summary>
    long BackdropSampleCount { get; }

    /// <summary>缓存占用字节数（性能面板用）。</summary>
    long CacheBytes { get; }
}

/// <summary>动画参数：统一时长与曲线，避免各控件自行硬编码。</summary>
public static class GlassAnimation
{
    /// <summary>常规过渡（悬停、状态切换）。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(160);

    /// <summary>标准过渡（面板淡入、滑块移动）。</summary>
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(260);

    /// <summary>弹性过渡（开关滑块、按压回弹）。</summary>
    public static readonly TimeSpan Spring = TimeSpan.FromMilliseconds(420);

    /// <summary>指针跟随高光的跟手速度（越小越快）。</summary>
    public static readonly TimeSpan PointerFollow = TimeSpan.FromMilliseconds(90);
}
