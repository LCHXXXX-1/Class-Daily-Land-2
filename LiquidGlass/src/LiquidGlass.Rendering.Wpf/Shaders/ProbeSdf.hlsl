// Diagnostic: rounded-rect mask only (proves SDF + uniforms).
sampler2D InputTexture : register(s0);
float4 SizeParams;    // CropSize.xy, ExpandPx, RadiusPx
float4 ShapeParams;   // HalfSize.xy, InvBand, EdgeBandPx

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 px = uv * SizeParams.xy - float2(SizeParams.z, SizeParams.z);
    float2 q = abs(px) - ShapeParams.xy + SizeParams.w;
    float2 outside = max(q, 0.0);
    float d = length(outside) + min(max(q.x, q.y), 0.0) - SizeParams.w;
    return float4(saturate(-d), saturate(d * 0.1), 0.2, 1.0);
}
