// Diagnostic: paint uv as color (proves TEXCOORD plumbing).
sampler2D InputTexture : register(s0);
float4 main(float2 uv : TEXCOORD) : COLOR
{
    return float4(uv.x, uv.y, 0.0, 1.0);
}
