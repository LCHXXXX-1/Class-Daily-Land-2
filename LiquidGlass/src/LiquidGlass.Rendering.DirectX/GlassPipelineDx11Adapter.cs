using System;

namespace LiquidGlass.Rendering.DirectX;

/// <summary>
/// 把 DX11 管线（<see cref="GlassGpuPipeline"/>）包成 <see cref="IGlassPipeline"/>。
/// </summary>
/// <remarks>
/// DX11 版内部是 Render + Readback 两步，这里合成一步，接口就和 DX12 版一致了。
/// 只在 DX12 设备创建失败时才会用到。
/// </remarks>
public sealed class GlassPipelineDx11Adapter : IGlassPipeline
{
    readonly GlassGpuPipeline _pipeline = new();
    byte[]? _scratch;

    public string BackendName => "DX11";

    public byte[] RenderAndReadback(byte[] backdropPixels, int w, int h, GlassRenderParams p)
    {
        _pipeline.Render(backdropPixels, w, h, p);
        var bmp = _pipeline.Readback(w, h);

        var bytes = w * h * 4;
        if (_scratch is null || _scratch.Length != bytes) _scratch = new byte[bytes];
        bmp.CopyPixels(_scratch, w * 4, 0);
        return _scratch;
    }

    public void Dispose() => _pipeline.Dispose();
}
