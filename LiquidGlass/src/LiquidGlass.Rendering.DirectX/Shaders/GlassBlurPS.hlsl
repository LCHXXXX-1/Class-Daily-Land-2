// GlassBlurPS.hlsl - separable Gaussian blur (ps_5_0)
// All parameters packed as float4 to avoid HLSL cbuffer packing ambiguity.
//
// Input is PREMULTIPLIED alpha (BGRA from RenderTargetBitmap / Pbgra32).
// Blurring premultiplied RGBA is the mathematically correct way to blur an
// image with transparency, so rgb and alpha are blurred together here.
Texture2D Input : register(t0);
SamplerState LinearSampler : register(s0);

cbuffer BlurParams : register(b0)
{
    float4 TexelDir;    // xy = texel size, zw = direction
    float4 Weights0;    // w[0..3]
    float4 Weights1;    // w[4..7]  (w[6] = center)
    float4 Weights2;    // w[8..11]
    float4 Weights3;    // w[12], stride, pad, pad
};

float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float2 texelSize = TexelDir.xy;
    float2 dir = TexelDir.zw;

    float w[13] = {
        Weights0.x, Weights0.y, Weights0.z, Weights0.w,
        Weights1.x, Weights1.y, Weights1.z, Weights1.w,
        Weights2.x, Weights2.y, Weights2.z, Weights2.w,
        Weights3.x
    };

    // stride: tap spacing in pixels. 13 taps with 1px spacing can only reach
    // sigma ~2; a bigger blur needs wider spacing (used by the full-window plate).
    float stride = max(Weights3.y, 1.0);

    float4 sum = Input.SampleLevel(LinearSampler, uv, 0) * w[6];

    [unroll]
    for (int i = 1; i <= 6; i++)
    {
        float2 offset = dir * texelSize * float(i) * stride;
        sum += Input.SampleLevel(LinearSampler, uv - offset, 0) * w[6 - i];
        sum += Input.SampleLevel(LinearSampler, uv + offset, 0) * w[6 + i];
    }

    return sum;
}
