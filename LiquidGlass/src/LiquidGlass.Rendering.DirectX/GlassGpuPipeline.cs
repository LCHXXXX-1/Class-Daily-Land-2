using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiquidGlass.Rendering.DirectX;

/// <summary>
/// 纯 GPU 玻璃渲染管线。
/// </summary>
/// <remarks>
/// 渲染流程（全部 GPU，零 CPU 端像素处理）：
///   Pass 1: 水平高斯模糊（13-tap 分离式）→ tempRT
///   Pass 2: 垂直高斯模糊 → frostRT
///   Pass 3: 玻璃效果（5-tap 折射 + Fresnel + 色调）→ outputRT
///
/// 资源管理：
///   - 常量缓冲区持久复用（Map/WriteDiscard/Unmap）
///   - 渲染目标按需调整大小，不每帧重建
///   - 纹理上传只在背景变化时发生
///
/// 对比旧版（CPU 路径）的改进：
///   ✅ 无 RenderTargetBitmap（CPU 软件光栅化）
///   ✅ 无 BlurEffect（CPU 模糊）
///   ✅ 无 CroppedBitmap / ImageBrush Viewbox（CPU 裁切）
///   ✅ 无每帧 BitmapSource.Create（GPU→CPU 回读）
///   ✅ 常量缓冲区 Map/Unmap（修复绑定问题）
/// </remarks>
public sealed class GlassGpuPipeline : IDisposable
{
    readonly GpuDevice _gpu;
    readonly ID3D11VertexShader _vs;
    readonly ID3D11PixelShader _blurPS;
    readonly ID3D11PixelShader _glassPS;

    // 中间渲染目标（按需重建）
    ID3D11Texture2D? _tempRT, _frostRT, _outputRT;
    ID3D11RenderTargetView? _tempRTV, _frostRTV, _outputRTV;
    ID3D11ShaderResourceView? _tempSRV, _frostSRV, _outputSRV;
    ID3D11Texture2D? _staging;
    int _width, _height;

    // D3DImage 共享句柄
    Format _outputFormat = Format.B8G8R8A8_UNorm;

    // 背景纹理缓存（只在变化时重建）
    ID3D11Texture2D? _bgTexture;
    ID3D11ShaderResourceView? _bgSRV;
    byte[]? _lastBgPixels;

    public GlassGpuPipeline()
    {
        _gpu = GpuDevice.Instance;

        // 编译着色器（嵌入资源）
        var blurSrc = LoadShader("GlassBlurPS.hlsl");
        var glassSrc = LoadShader("GlassEffectPS.hlsl");
        var vsSrc = "struct O{float4 p:SV_Position;float2 u:TEXCOORD;};O main(uint i:SV_VertexID){O o;float2 uv=float2((i<<1)&2,i&2);o.p=float4(uv*float2(2,-2)+float2(-1,1),0,1);o.u=uv;return o;}";

        _vs = _gpu.Device.CreateVertexShader(_gpu.CompileShader(vsSrc, "main", "vs_5_0"));
        _blurPS = _gpu.Device.CreatePixelShader(_gpu.CompileShader(blurSrc, "main", "ps_5_0"));
        _glassPS = _gpu.Device.CreatePixelShader(_gpu.CompileShader(glassSrc, "main", "ps_5_0"));
    }

    /// <summary>
    /// 渲染玻璃效果到输出纹理（GPU→GPU，无 CPU 回读）。
    /// </summary>
    /// <param name="backdropPixels">背景像素（BGRA32）。</param>
    /// <param name="w">渲染宽度。</param>
    /// <param name="h">渲染高度。</param>
    /// <param name="p">玻璃参数。</param>
    /// <returns>输出纹理的 SRV（可直接绑定到 D3DImage 或后续 pass）。</returns>
    public ID3D11ShaderResourceView Render(byte[] backdropPixels, int w, int h, GlassRenderParams p)
    {
        EnsureSize(w, h);
        UploadBackdrop(backdropPixels, w, h);

        var ctx = _gpu.Context;
        ctx.RSSetViewport(new Viewport(0, 0, w, h));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetSampler(0, _gpu.LinearSampler);

        // ---- Pass 1: 水平模糊 ----
        // ★ 关键：先 PSSetShader 再 UpdateCB（绑定 cbuffer）。
        // Vortice 的 PSSetShader 可能重置 cbuffer 绑定，所以必须在 shader 设置之后绑定。
        if (p.BlurRadius > 0.5f)
        {
            using var cbH = UpdateBlurCB(w, h, 1, 0, p.BlurRadius);  // 先绑 cbuffer
            ctx.PSSetShader(_blurPS);  // 再设 shader
            ctx.ClearRenderTargetView(_tempRTV!, new Color4(0, 0, 0, 1));
            ctx.OMSetRenderTargets(_tempRTV!);
            ctx.PSSetShaderResource(0, _bgSRV!);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(0, null);

            // ---- Pass 2: 垂直模糊 ----
            using var cbV = UpdateBlurCB(w, h, 0, 1, p.BlurRadius);
            ctx.PSSetShader(_blurPS);
            ctx.ClearRenderTargetView(_frostRTV!, new Color4(0, 0, 0, 1));
            ctx.OMSetRenderTargets(_frostRTV!);
            ctx.PSSetShaderResource(0, _tempSRV!);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(0, null);
        }
        else
        {
            // 不模糊：直接把背景当 frost（单次 passthrough）
            using var cbP = UpdateBlurCB(w, h, 0, 0, 0);
            ctx.PSSetShader(_blurPS);
            ctx.ClearRenderTargetView(_frostRTV!, new Color4(0, 0, 0, 1));
            ctx.OMSetRenderTargets(_frostRTV!);
            ctx.PSSetShaderResource(0, _bgSRV!);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResource(0, null);
        }

        // 诊断：读 frost 看 blur 输出
        if (Environment.GetEnvironmentVariable("LG_CB_DEBUG") == "1")
        {
            ctx.CopyResource(_staging!, _frostRT!);
            ctx.Flush();
            var sm = ctx.Map(_staging!, 0, MapMode.Read);
            unsafe
            {
                var bp = (byte*)sm.DataPointer;
                int off = ((h/2) * w + w/2) * 4;
                Console.WriteLine($"[FROST] center: B={bp[off]} G={bp[off+1]} R={bp[off+2]} A={bp[off+3]}");
            }
            ctx.Unmap(_staging!, 0);
        }

        // ---- Pass 3: 玻璃效果 ----
        using var cbG = UpdateGlassCB(w, h, p);  // 先绑 cbuffer
        ctx.PSSetShader(_glassPS);  // 再设 shader
        ctx.ClearRenderTargetView(_outputRTV!, new Color4(0, 0, 0, 1));
        ctx.OMSetRenderTargets(_outputRTV!);
        ctx.PSSetShaderResource(0, _frostSRV!);
        ctx.PSSetShaderResource(1, _bgSRV!);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null);
        ctx.PSSetShaderResource(1, null);
        ctx.Flush();

        return _outputSRV!;
    }

    /// <summary>
    /// GPU→CPU 回读（仅用于调试/截图；正常运行时用 D3DImage 零拷贝显示）。
    /// </summary>
    public BitmapSource Readback(int w, int h)
    {
        var ctx = _gpu.Context;
        ctx.CopyResource(_staging!, _outputRT!);
        ctx.Flush();

        var mapped = ctx.Map(_staging!, 0, MapMode.Read);
        var pixels = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var src = mapped.DataPointer;
            var dst = handle.AddrOfPinnedObject();
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    src + y * (int)mapped.RowPitch,
                    pixels, y * w * 4, w * 4);
        }
        finally
        {
            ctx.Unmap(_staging!, 0);
            handle.Free();
        }

        // ★ 着色器输出的是**预乘 alpha**，这里必须按 Pbgra32 解释。
        //   以前用 Bgra32（直通 alpha）会让边缘一圈像素被重复乘一次 alpha，
        //   在胶囊轮廓上显示成一条灰边。
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---- 内部 ----

    void EnsureSize(int w, int h)
    {
        if (_width == w && _height == h && _outputRT is not null) return;

        _tempRT?.Dispose(); _tempRTV?.Dispose(); _tempSRV?.Dispose();
        _frostRT?.Dispose(); _frostRTV?.Dispose(); _frostSRV?.Dispose();
        _outputRT?.Dispose(); _outputRTV?.Dispose(); _outputSRV?.Dispose();
        _staging?.Dispose();

        _width = w; _height = h;

        _tempRT = _gpu.CreateRenderTarget(w, h);
        _tempRTV = _gpu.CreateRTV(_tempRT);
        _tempSRV = _gpu.CreateSRV(_tempRT);

        _frostRT = _gpu.CreateRenderTarget(w, h);
        _frostRTV = _gpu.CreateRTV(_frostRT);
        _frostSRV = _gpu.CreateSRV(_frostRT);

        _outputRT = _gpu.CreateRenderTarget(w, h, _outputFormat);
        _outputRTV = _gpu.CreateRTV(_outputRT);
        _outputSRV = _gpu.CreateSRV(_outputRT);

        _staging = _gpu.CreateStaging(w, h);
    }

    void UploadBackdrop(byte[] pixels, int w, int h)
    {
        // 只在背景变化时重建纹理
        if (_lastBgPixels is not null && _bgTexture is not null
            && _lastBgPixels.Length == pixels.Length
            && ReferenceEquals(_lastBgPixels, pixels))
            return;

        _bgSRV?.Dispose();
        _bgTexture?.Dispose();

        _bgTexture = _gpu.CreateTextureFromBytes(pixels, w, h);
        _bgSRV = _gpu.CreateSRV(_bgTexture);
        _lastBgPixels = pixels;
    }

    ID3D11Buffer UpdateBlurCB(int w, int h, float dirX, float dirY, float radius)
    {
        var sigma = Math.Max(radius * 0.5f, 0.5f);
        var weights = new float[13];
        float sum = 0;
        for (int i = 0; i < 13; i++)
        {
            float x = i - 6;
            weights[i] = MathF.Exp(-x * x / (2 * sigma * sigma));
            sum += weights[i];
        }
        for (int i = 0; i < 13; i++) weights[i] /= sum;

        var data = new float[20];
        data[0] = 1.0f / w;
        data[1] = 1.0f / h;
        data[2] = dirX;
        data[3] = dirY;
        for (int i = 0; i < 13; i++) data[4 + i] = weights[i];
        data[17] = Math.Clamp(radius / 3f, 1f, 10f);   // tap 间距：大半径才铺得开

        return _gpu.CreateAndBindCB(data);
    }

    ID3D11Buffer UpdateGlassCB(int w, int h, GlassRenderParams p)
    {
        var data = new float[48];
        data[0] = w; data[1] = h;
        data[2] = p.HalfWidth; data[3] = p.HalfHeight;
        data[4] = p.CornerRadius;
        data[5] = 1.0f / Math.Max(p.RefractBand, 1f);
        data[6] = 0.5f;  // AAScale (softer 2px edge, lets EdgeOpacity read as a rim)
        data[7] = 0;
        data[8] = p.RefractPixels / w;
        data[9] = p.RefractPixels / h;
        data[10] = p.Dispersion;
        data[11] = p.RimStrength;
        data[12] = p.FillOpacity;
        data[13] = p.ToneWeight;
        data[14] = p.ShadowLift;
        data[15] = 1.0f / Math.Max(p.RimWidth, 0.25f);
        data[16] = p.ToneA; data[17] = p.ToneB; data[18] = p.ToneQ;
        data[19] = p.TintOpacity;
        data[20] = p.TintR; data[21] = p.TintG; data[22] = p.TintB;
        data[23] = p.FresnelPower;
        data[24] = p.InnerGlow;
        data[25] = p.SolidOpacity;
        data[26] = p.EdgeOpacity;
        data[27] = p.Coverage;

        return _gpu.CreateAndBindCB(data);
    }

    static string LoadShader(string name)
    {
        var asm = typeof(GlassGpuPipeline).Assembly;
        var resName = $"LiquidGlass.Rendering.DirectX.Shaders.{name}";
        using var stream = asm.GetManifestResourceStream(resName);
        if (stream is not null)
        {
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
        throw new System.IO.FileNotFoundException($"找不到着色器: {name}");
    }

    public void Dispose()
    {
        _tempRT?.Dispose(); _tempRTV?.Dispose(); _tempSRV?.Dispose();
        _frostRT?.Dispose(); _frostRTV?.Dispose(); _frostSRV?.Dispose();
        _outputRT?.Dispose(); _outputRTV?.Dispose(); _outputSRV?.Dispose();
        _staging?.Dispose();
        _bgTexture?.Dispose(); _bgSRV?.Dispose();
        _blurPS?.Dispose(); _glassPS?.Dispose(); _vs?.Dispose();
    }
}

/// <summary>玻璃渲染参数（GPU uniform 格式）。</summary>
public struct GlassRenderParams
{
    public float HalfWidth, HalfHeight;
    public float CornerRadius;
    public float RefractBand;
    public float RefractPixels;
    public float Dispersion;
    public float RimStrength;
    public float RimWidth;
    public float BlurRadius;
    public float FillOpacity;
    public float ToneWeight;
    public float ShadowLift;
    public float ToneA, ToneB, ToneQ;
    public float TintR, TintG, TintB;
    public float TintOpacity;
    public float FresnelPower;
    public float InnerGlow;
    /// <summary>玻璃自身的薄白不透明度（背景透明时由它撑起"材质"）。</summary>
    public float SolidOpacity;
    /// <summary>折射带内额外的本体不透明度（读出玻璃厚度）。</summary>
    public float EdgeOpacity;
    /// <summary>接管背板的程度（整窗背景板 = 1）。</summary>
    public float Coverage;

    public static GlassRenderParams FromMaterial(LiquidGlass.Core.GlassParameters m, int pixelW, int pixelH)
    {
        return new GlassRenderParams
        {
            HalfWidth = pixelW * 0.5f,
            HalfHeight = pixelH * 0.5f,
            CornerRadius = (float)m.CornerRadius,
            RefractBand = (float)m.RefractionBand,
            RefractPixels = (float)m.RefractionPixels,
            Dispersion = (float)m.Dispersion,
            RimStrength = (float)m.RimStrength,
            RimWidth = (float)m.RimWidth,
            BlurRadius = (float)m.BlurSigma,
            FillOpacity = (float)m.FillOpacity,
            ToneWeight = (float)m.ToneWeight,
            ShadowLift = (float)m.ShadowLift,
            ToneA = (float)m.ToneA,
            ToneB = (float)m.ToneB,
            ToneQ = (float)m.ToneQ,
            TintR = 1, TintG = 1, TintB = 1,
            TintOpacity = 0,
            FresnelPower = 2.5f,
            InnerGlow = 0.055f,
            SolidOpacity = (float)m.SurfaceOpacity,
            EdgeOpacity = (float)m.EdgeOpacity,
            Coverage = (float)m.Coverage,
        };
    }
}
