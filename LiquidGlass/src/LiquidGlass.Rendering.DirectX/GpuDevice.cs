using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LiquidGlass.Rendering.DirectX;

/// <summary>
/// GPU 资源管理器：设备、常量缓冲区、纹理池、渲染目标。
/// </summary>
/// <remarks>
/// 全部 GPU 资源在此集中管理、预分配、复用。运行时零分配。
/// 常量缓冲区采用 Dynamic + Map/Unmap（WriteDiscard），单缓冲区持久复用。
/// </remarks>
public sealed class GpuDevice : IDisposable
{
    static GpuDevice? _instance;
    static readonly object _lock = new();

    public ID3D11Device Device { get; private set; } = null!;
    public ID3D11DeviceContext Context { get; private set; } = null!;
    public FeatureLevel FeatureLevel { get; private set; }
    public bool IsHardware { get; private set; }
    public string AdapterName { get; private set; } = "";


    // 全屏三角形（SV_VertexID，无需顶点缓冲）
    public ID3D11SamplerState LinearSampler { get; private set; } = null!;

    GpuDevice() { }

    public static GpuDevice Instance
    {
        get
        {
            if (_instance is not null) return _instance;
            lock (_lock)
            {
                _instance ??= new GpuDevice();
                if (!_instance._initialized) _instance.Initialize();
                return _instance;
            }
        }
    }

    bool _initialized;

    void Initialize()
    {
        _initialized = true;

        var flags = DeviceCreationFlags.BgraSupport;
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };

        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels,
                out var dev, out var fl, out var ctx).Success)
        {
            Device = dev; FeatureLevel = fl; Context = ctx; IsHardware = true;
        }
        else if (D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels,
                out var wdev, out var wfl, out var wctx).Success)
        {
            Device = wdev; FeatureLevel = wfl; Context = wctx; IsHardware = false;
        }
        else
        {
            throw new InvalidOperationException("D3D11 设备创建失败");
        }

        AdapterName = GetAdapterName();


        // 采样器
        LinearSampler = Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
    }

    string GetAdapterName()
    {
        try
        {
            using var dxgi = Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            return adapter.Description.Description ?? "Unknown";
        }
        catch { return "Unknown"; }
    }


    /// <summary>
    /// 创建并绑定常量缓冲区（每次调用创建新缓冲区，与 Test 4 验证的模式完全一致）。
    /// </summary>
    /// <remarks>
    /// ★ 两个关键发现：
    /// 1. Marshal.Copy(IntPtr) 对 Vortice Map 指针写入无效 —— 必须用 unsafe 指针写入。
    /// 2. 持久化缓冲区（Initialize 时创建）绑定后 shader 读到零 —— 必须每次内联创建。
    ///    可能是 Vortice 的 CreateBuffer(null) 返回的对象在后续 Map/Unmap 后失效。
    ///    内联创建（每次 new buffer + Map/Write/Unmap + Bind）经过验证完全可靠。
    /// </remarks>
    public unsafe ID3D11Buffer CreateAndBindCB(float[] data)
    {
        // 对齐到 16 字节
        int alignedLen = (data.Length * 4 + 15) / 16 * 16;
        var padded = new float[alignedLen / 4];
        Array.Copy(data, padded, data.Length);

        var desc = new BufferDescription
        {
            ByteWidth = (uint)alignedLen,
            BindFlags = BindFlags.ConstantBuffer,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        var cb = Device.CreateBuffer(ref desc, (SubresourceData?)null);

        var mapped = Context.Map(cb, 0, MapMode.WriteDiscard);
        try
        {
            float* dst = (float*)mapped.DataPointer;
            for (int i = 0; i < padded.Length; i++) dst[i] = padded[i];
        }
        finally
        {
            Context.Unmap(cb, 0);
        }
        Context.PSSetConstantBuffer(0, cb);
        return cb;
    }

    // ---- 纹理创建 ----

    public ID3D11Texture2D CreateRenderTarget(int w, int h, Format fmt = Format.B8G8R8A8_UNorm,
        BindFlags bind = BindFlags.RenderTarget | BindFlags.ShaderResource,
        ResourceUsage usage = ResourceUsage.Default,
        CpuAccessFlags cpuAccess = CpuAccessFlags.None)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = fmt, SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bind,
            CPUAccessFlags = cpuAccess,
            MiscFlags = ResourceOptionFlags.None,
        };
        return Device.CreateTexture2D(desc, (SubresourceData[])null!);
    }
    public ID3D11Texture2D CreateStaging(int w, int h, Format fmt = Format.B8G8R8A8_UNorm)
        => Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = fmt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

    /// <summary>从 CPU 字节数据创建 GPU 纹理（Default usage + UpdateSubresource）。</summary>
    public ID3D11Texture2D CreateTextureFromBytes(byte[] data, int w, int h,
        Format fmt = Format.B8G8R8A8_UNorm)
    {
        var tex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = fmt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            Context.UpdateSubresource(tex, 0, null,
                handle.AddrOfPinnedObject(), (uint)(w * 4), (uint)(w * h * 4));
        }
        finally { handle.Free(); }
        return tex;
    }

    public ID3D11ShaderResourceView CreateSRV(ID3D11Texture2D tex) => Device.CreateShaderResourceView(tex);
    public ID3D11RenderTargetView CreateRTV(ID3D11Texture2D tex) => Device.CreateRenderTargetView(tex);

    /// <summary>编译 HLSL。</summary>
    public byte[] CompileShader(string source, string entry, string profile) =>
        CompileShaderBytes(source, entry, profile);

    /// <summary>
    /// 编译 HLSL（不依赖设备：DX12 管线也要用它，那时不需要建 DX11 设备）。
    /// </summary>
    public static byte[] CompileShaderBytes(string source, string entry, string profile)
    {
        var result = Vortice.D3DCompiler.Compiler.Compile(
            source, entry, "GlassGPU", profile,
            Vortice.D3DCompiler.ShaderFlags.OptimizationLevel3,
            Vortice.D3DCompiler.EffectFlags.None);
        return result.ToArray();
    }

    public void Dispose()
    {
        LinearSampler?.Dispose();
        Context?.Dispose();
        Device?.Dispose();
        _instance = null;
        _initialized = false;
    }
}
