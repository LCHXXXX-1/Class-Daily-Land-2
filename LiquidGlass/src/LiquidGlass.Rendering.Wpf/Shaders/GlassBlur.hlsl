// ============================================================================
//  GlassBlur.hlsl - separable gaussian blur (shared backdrop pre-pass)
//
//  Why a separate pass: ps_2_0 only has 32 texture instructions, so blur +
//  refraction + dispersion + rim light cannot fit in a single shader. The blur
//  runs once per frame on the shared backdrop texture; the refraction pass
//  then samples that already-blurred texture.
//
//  NOTE: keep this file ASCII-only. D3DCompiler is invoked with a byte count
//  derived from character count, so non-ASCII comments truncate the source.
//
//  Sampling: 9 taps with hardware bilinear (2 texels per tap). Running it
//  horizontally then vertically gives an effective 17x17 gaussian.
// ============================================================================

sampler2D InputTexture : register(s0);

float2 TexelStep;      // uv step of one texel (y component is 0 on the horizontal pass)
float  BlurStrength;   // 0..1 scale of the sampling distance

// Gaussian weights, sigma ~= 2.6, normalized to sum 1.
static const float W0 = 0.2270270270;
static const float W1 = 0.1945945946;
static const float W2 = 0.1216216216;
static const float W3 = 0.0540540541;
static const float W4 = 0.0162162162;

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // Radius scales to 8 texels at BlurStrength = 1; 0 degenerates to a copy.
    float2 step0 = TexelStep * BlurStrength;

    float4 sum = tex2D(InputTexture, uv) * W0;

    // Bilinear trick: sample at +/-1.5, 3.5, 5.5, 7.5 texels to cover +/-8.
    sum += (tex2D(InputTexture, uv + step0 * 1.5) + tex2D(InputTexture, uv - step0 * 1.5)) * W1;
    sum += (tex2D(InputTexture, uv + step0 * 3.5) + tex2D(InputTexture, uv - step0 * 3.5)) * W2;
    sum += (tex2D(InputTexture, uv + step0 * 5.5) + tex2D(InputTexture, uv - step0 * 5.5)) * W3;
    sum += (tex2D(InputTexture, uv + step0 * 7.5) + tex2D(InputTexture, uv - step0 * 7.5)) * W4;

    return sum;
}
