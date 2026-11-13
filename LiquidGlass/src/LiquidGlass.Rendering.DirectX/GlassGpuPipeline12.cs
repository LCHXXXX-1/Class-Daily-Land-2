using System;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiquidGlass.Rendering.DirectX;

/// <summary>
/// Direct3D 12 玻璃渲染管线。
/// </summary>
/// <remarks>
/// 与 DX11 版（<see cref="GlassGpuPipeline"/>）用同一套着色器、同一套常量布局、同一种输出格式：
///   Pass 1: 水平 13-tap 高斯 → tempRT
///   Pass 2: 垂直 13-tap 高斯 → frostRT
///   Pass 3: 折射 / 色散 / 边光 / alpha 合成 → outRT
///
/// DX12 要自己管的东西（DX11 由运行时兜着的）：
///   - 没有"设备上下文"，所有状态记在命令列表里，最后一并提交
///   - 资源状态迁移（ResourceBarrier）：RTV ↔ SRV ↔ COPY_SOURCE
///   - 常量直接用 **root constants**（32 bit 值），不必建 CBV
///   - 回读必须 CopyResource 到 READBACK 堆再 Map，行距按 256 字节对齐
///
/// 每帧末尾同步等 GPU（fence）：WPF 侧要立刻拿到像素。胶囊只有几十 KB，
/// 这点同步开销远小于一帧 16ms，换来的是"渲染完即可用"的简单模型。
/// </remarks>
public sealed class GlassGpuPipeline12 : IGlassPipeline
{
    const int SrvCount = 2;
    const int RtvCount = 3;
    const int RootConstantCount = 32;      // glass CB 用了 28 个 float

    readonly ID3D12Device _device;
    readonly ID3D12CommandQueue _queue;
    readonly ID3D12CommandAllocator _allocator;
    readonly ID3D12GraphicsCommandList _cmd;
    readonly ID3D12Fence _fence;
    readonly AutoResetEvent _fenceEvent = new(false);
    readonly ID3D12RootSignature _rootSig;
    readonly ID3D12PipelineState _blurPso;
    readonly ID3D12PipelineState _glassPso;
    readonly ID3D12DescriptorHeap _srvHeap;
    readonly ID3D12DescriptorHeap _rtvHeap;
    readonly int _srvStride;
    readonly int _rtvStride;

    ulong _fenceValue = 1;

    // 按尺寸创建的资源
    ID3D12Resource? _bgTex, _tempRt, _frostRt, _outRt, _readback, _upload;
    int _w, _h, _uploadRowPitch;

    public string AdapterName { get; }
    public bool IsHardware { get; }
    public string BackendName => $"DX12 ({AdapterName})";

    public GlassGpuPipeline12()
    {
        (_device, IsHardware) = CreateDevice();
        AdapterName = QueryAdapterName(_device);

        _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        _allocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _fence = _device.CreateFence(0, FenceFlags.None);

        _rootSig = CreateRootSignature(_device);

        var vs = GpuDevice.CompileShaderBytes(FullscreenVs, "main", "vs_5_1");
        var blurPs = GpuDevice.CompileShaderBytes(LoadShader("GlassBlurPS.hlsl"), "main", "ps_5_1");
        var glassPs = GpuDevice.CompileShaderBytes(LoadShader("GlassEffectPS.hlsl"), "main", "ps_5_1");

        _blurPso = CreatePipeline(_device, _rootSig, vs, blurPs);
        _glassPso = CreatePipeline(_device, _rootSig, vs, glassPs);

        _cmd = _device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocator, _blurPso);
        _cmd.Close();

        _srvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, SrvCount,
            DescriptorHeapFlags.None, 0));
        _srvStride = (int)_device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        _rtvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, RtvCount, DescriptorHeapFlags.None, 0));
        _rtvStride = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
    }

    // ---- 设备 / 根签名 / PSO ----

    static (ID3D12Device, bool) CreateDevice()
    {
        // 逐个适配器试（枚举顺序 = 独显优先），拿到的第一个能建 DX12 设备的就用它
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure || adapter is null) break;
                try
                {
                    if (D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? dev).Success
                        && dev is not null)
                        return (dev, true);
                }
                finally { adapter.Dispose(); }
            }
        }
        catch { }

        // 一个都不行就退默认适配器（可能是 WARP 软件光栅）
        try
        {
            if (D3D12.D3D12CreateDevice(null, FeatureLevel.Level_11_0, out ID3D12Device? fallback).Success
                && fallback is not null)
                return (fallback, false);
        }
        catch { }

        throw new InvalidOperationException("D3D12 设备创建失败");
    }

    static string QueryAdapterName(ID3D12Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            return adapter.Description.Description ?? "Unknown";
        }
        catch { return "Unknown"; }
    }

    static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        var parameters = new[]
        {
            // b0：root constants（玻璃 28 个 float / 模糊 20 个）
            new RootParameter(new RootConstants(0, 0, RootConstantCount), ShaderVisibility.Pixel),
            // t0 / t1：一张描述符表
            new RootParameter(
                new RootDescriptorTable(new[] { new DescriptorRange(DescriptorRangeType.ShaderResourceView, SrvCount, 0, 0, 0) }),
                ShaderVisibility.Pixel),
        };

        var sampler = new StaticSamplerDescription(
            0, Filter.MinMagMipLinear,
            TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            0f, 1, ComparisonFunction.Never, StaticBorderColor.TransparentBlack,
            0f, float.MaxValue, ShaderVisibility.Pixel, 0);

        var desc = new RootSignatureDescription(RootSignatureFlags.None, parameters, new[] { sampler });
        return device.CreateRootSignature(desc, RootSignatureVersion.Version1);
    }

    static ID3D12PipelineState CreatePipeline(ID3D12Device device, ID3D12RootSignature root, byte[] vs, byte[] ps)
    {
        var desc = new GraphicsPipelineStateDescription
        {
            RootSignature = root,
            VertexShader = vs,
            PixelShader = ps,
            RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = false },
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        return device.CreateGraphicsPipelineState(desc);
    }

    // ---- 尺寸相关资源 ----

    void EnsureSize(int w, int h)
    {
        if (_w == w && _h == h && _outRt is not null) return;

        _bgTex?.Dispose(); _tempRt?.Dispose(); _frostRt?.Dispose();
        _outRt?.Dispose(); _readback?.Dispose(); _upload?.Dispose();

        _w = w; _h = h;
        _uploadRowPitch = AlignTo(w * 4, D3D12.TextureDataPitchAlignment);

        _bgTex = CreateTexture(w, h, ResourceStates.CopyDest, ResourceFlags.AllowRenderTarget);
        _tempRt = CreateTexture(w, h, ResourceStates.RenderTarget, ResourceFlags.AllowRenderTarget);
        _frostRt = CreateTexture(w, h, ResourceStates.RenderTarget, ResourceFlags.AllowRenderTarget);
        _outRt = CreateTexture(w, h, ResourceStates.CopySource, ResourceFlags.AllowRenderTarget);

        _readback = _device.CreateCommittedResource(HeapType.Readback,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, 1, 0,
                ResourceFlags.None, TextureLayout.RowMajor, 0),
            ResourceStates.CopyDest, null);

        _upload = _device.CreateCommittedResource(HeapType.Upload,
            ResourceDescription.Buffer((ulong)(_uploadRowPitch * h), ResourceFlags.None, 0),
            ResourceStates.GenericRead, null);

        CreateRtv(0, _tempRt);
        CreateRtv(1, _frostRt);
        CreateRtv(2, _outRt);
    }

    ID3D12Resource CreateTexture(int w, int h, ResourceStates states, ResourceFlags flags) =>
        _device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, 1, 0,
                flags, TextureLayout.RowMajor, 0),
            states, null);

    void CreateRtv(int slot, ID3D12Resource resource)
    {
        var handle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        handle += slot * _rtvStride;
        _device.CreateRenderTargetView(resource, null, handle);
    }

    CpuDescriptorHandle Rtv(int slot)
    {
        var handle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        return handle + slot * _rtvStride;
    }

    static int AlignTo(int v, int alignment) => (v + alignment - 1) / alignment * alignment;

    // ---- 渲染 ----

    public byte[] RenderAndReadback(byte[] backdropPixels, int w, int h, GlassRenderParams p)
    {
        EnsureSize(w, h);

        _allocator.Reset();
        _cmd.Reset(_allocator, _blurPso);
        _cmd.SetDescriptorHeaps(_srvHeap);
        _cmd.SetGraphicsRootSignature(_rootSig);
        _cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _cmd.RSSetViewport(new Viewport(0, 0, w, h));
        var scissor = new RectI(0, 0, w, h);
        _cmd.RSSetScissorRect(ref scissor);

        UploadBackdrop(backdropPixels, w, h);

        // ---- Pass 1: 水平模糊 → temp ----
        _cmd.SetPipelineState(_blurPso);
        _cmd.OMSetRenderTargets(Rtv(0), null);
        SetSrvPair(_bgTex!, _bgTex!);
        _cmd.SetGraphicsRoot32BitConstants(0, BlurConstants(w, h, 1, 0, p.BlurRadius), 0);
        _cmd.DrawInstanced(3, 1, 0, 0);

        // ---- Pass 2: 垂直模糊 → frost ----
        _cmd.ResourceBarrierTransition(_tempRt!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource, 0, ResourceBarrierFlags.None);
        _cmd.OMSetRenderTargets(Rtv(1), null);
        SetSrvPair(_tempRt!, _tempRt!);
        _cmd.SetGraphicsRoot32BitConstants(0, BlurConstants(w, h, 0, 1, p.BlurRadius), 0);
        _cmd.DrawInstanced(3, 1, 0, 0);

        // ---- Pass 3: 玻璃 → out ----
        _cmd.ResourceBarrierTransition(_frostRt!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource, 0, ResourceBarrierFlags.None);
        _cmd.SetPipelineState(_glassPso);
        _cmd.OMSetRenderTargets(Rtv(2), null);
        SetSrvPair(_frostRt!, _bgTex!);
        _cmd.SetGraphicsRoot32BitConstants(0, GlassConstants(w, h, p), 0);
        _cmd.DrawInstanced(3, 1, 0, 0);

        // ---- 回读 ----
        _cmd.CopyResource(_readback!, _outRt!);

        // 下一帧还要当渲染目标用，状态回退
        _cmd.ResourceBarrierTransition(_tempRt!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget, 0, ResourceBarrierFlags.None);
        _cmd.ResourceBarrierTransition(_frostRt!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget, 0, ResourceBarrierFlags.None);

        _cmd.Close();
        _queue.ExecuteCommandList(_cmd);
        WaitForGpu();

        return ReadbackPixels(w, h);
    }

    void UploadBackdrop(byte[] pixels, int w, int h)
    {
        var rowBytes = w * 4;
        var span = _upload!.Map<byte>(0, _uploadRowPitch * h);
        for (var y = 0; y < h; y++)
            pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(span.Slice(y * _uploadRowPitch, rowBytes));
        _upload.Unmap(0);

        _cmd.CopyTextureRegion(new TextureCopyLocation(_bgTex!, 0), 0, 0, 0,
            new TextureCopyLocation(_upload, new PlacedSubresourceFootPrint
            {
                Offset = 0,
                Footprint = new SubresourceFootPrint(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, (uint)_uploadRowPitch),
            }), null);

        _cmd.ResourceBarrierTransition(_bgTex!, ResourceStates.CopyDest, ResourceStates.PixelShaderResource, 0, ResourceBarrierFlags.None);
    }

    byte[] ReadbackPixels(int w, int h)
    {
        var rowBytes = w * 4;
        var pitch = AlignTo(rowBytes, D3D12.TextureDataPitchAlignment);
        var pixels = new byte[w * h * 4];
        var span = _readback!.Map<byte>(0, pitch * h);
        for (var y = 0; y < h; y++)
            span.Slice(y * pitch, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        _readback.Unmap(0);
        return pixels;
    }

    void SetSrvPair(ID3D12Resource t0, ID3D12Resource t1)
    {
        var cpu = _srvHeap.GetCPUDescriptorHandleForHeapStart();
        CreateSrv(cpu, t0);
        CreateSrv(cpu + _srvStride, t1);
        _cmd.SetGraphicsRootDescriptorTable(1, _srvHeap.GetGPUDescriptorHandleForHeapStart());
    }

    void CreateSrv(CpuDescriptorHandle handle, ID3D12Resource resource) =>
        _device.CreateShaderResourceView(resource, new ShaderResourceViewDescription
        {
            Format = Format.B8G8R8A8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, handle);

    void WaitForGpu()
    {
        var value = _fenceValue++;
        _queue.Signal(_fence, value);
        if (_fence.CompletedValue < value)
        {
            _fence.SetEventOnCompletion(value, _fenceEvent);
            _fenceEvent.WaitOne(2000);
        }
    }

    // ---- 常量（与 HLSL cbuffer 一一对应，全部按 float4 打包） ----

    static float[] BlurConstants(int w, int h, float dirX, float dirY, float radius)
    {
        var sigma = Math.Max(radius * 0.5f, 0.5f);
        var weights = new float[13];
        float sum = 0;
        for (var i = 0; i < 13; i++)
        {
            float x = i - 6;
            weights[i] = MathF.Exp(-x * x / (2 * sigma * sigma));
            sum += weights[i];
        }
        for (var i = 0; i < 13; i++) weights[i] /= sum;

        var data = new float[20];
        data[0] = 1.0f / w;
        data[1] = 1.0f / h;
        data[2] = dirX;
        data[3] = dirY;
        for (var i = 0; i < 13; i++) data[4 + i] = weights[i];
        data[17] = Math.Clamp(radius / 3f, 1f, 10f);
        return data;
    }

    static float[] GlassConstants(int w, int h, GlassRenderParams p)
    {
        var data = new float[28];
        data[0] = w; data[1] = h;
        data[2] = p.HalfWidth; data[3] = p.HalfHeight;
        data[4] = p.CornerRadius;
        data[5] = 1.0f / Math.Max(p.RefractBand, 1f);
        data[6] = 0.5f;   // AAScale：软 2px 边，让 EdgeOpacity 读成"厚度"而不是硬描边
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
        return data;
    }

    // ---- 着色器 ----

    const string FullscreenVs =
        "struct O{float4 p:SV_Position;float2 u:TEXCOORD;};" +
        "O main(uint i:SV_VertexID){O o;float2 uv=float2((i<<1)&2,i&2);" +
        "o.p=float4(uv*float2(2,-2)+float2(-1,1),0,1);o.u=uv;return o;}";

    static string LoadShader(string name)
    {
        var asm = typeof(GlassGpuPipeline12).Assembly;
        using var stream = asm.GetManifestResourceStream($"LiquidGlass.Rendering.DirectX.Shaders.{name}");
        if (stream is not null)
        {
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
        throw new System.IO.FileNotFoundException($"找不到着色器: {name}");
    }

    public void Dispose()
    {
        _bgTex?.Dispose(); _tempRt?.Dispose(); _frostRt?.Dispose();
        _outRt?.Dispose(); _readback?.Dispose(); _upload?.Dispose();
        _rtvHeap.Dispose(); _srvHeap.Dispose();
        _glassPso.Dispose(); _blurPso.Dispose(); _rootSig.Dispose();
        _fence.Dispose(); _cmd.Dispose(); _allocator.Dispose(); _queue.Dispose();
        _device.Dispose();
        _fenceEvent.Dispose();
    }
}
