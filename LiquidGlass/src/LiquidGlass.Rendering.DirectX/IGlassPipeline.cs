using System;

namespace LiquidGlass.Rendering.DirectX;

/// <summary>
/// 玻璃渲染管线的统一接口：喂背景像素，拿回渲染好的 BGRA（预乘 alpha）像素。
/// </summary>
/// <remarks>
/// 抽这层是为了让 <see cref="D3DGlassLayer"/> 不关心后端：
/// 首选 DX12（<see cref="GlassGpuPipeline12"/>），DX12 设备创建失败时退回
/// DX11（<see cref="GlassGpuPipeline"/>，老机器 / 老驱动 / 远程桌面）。
/// 两者的着色器、常量布局、输出格式完全一致，所以视觉上应当一样。
/// </remarks>
public interface IGlassPipeline : IDisposable
{
    /// <summary>后端名字（用于日志/诊断，例如 "DX12 (NVIDIA ...)"）。</summary>
    string BackendName { get; }

    /// <summary>
    /// 渲染一块玻璃并回读像素。
    /// </summary>
    /// <param name="backdropPixels">背景像素（BGRA32，预乘 alpha）。</param>
    /// <param name="w">宽度（像素）。</param>
    /// <param name="h">高度（像素）。</param>
    /// <param name="p">玻璃参数。</param>
    /// <returns>BGRA32 预乘 alpha 的结果像素，长度 = w*h*4。</returns>
    byte[] RenderAndReadback(byte[] backdropPixels, int w, int h, GlassRenderParams p);
}
