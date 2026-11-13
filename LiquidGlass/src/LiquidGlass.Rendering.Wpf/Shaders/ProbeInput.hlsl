// Diagnostic: return the implicit input sampler untouched (proves s0 binding).
sampler2D InputTexture : register(s0);
float4 main(float2 uv : TEXCOORD) : COLOR
{
    return tex2D(InputTexture, uv);
}
