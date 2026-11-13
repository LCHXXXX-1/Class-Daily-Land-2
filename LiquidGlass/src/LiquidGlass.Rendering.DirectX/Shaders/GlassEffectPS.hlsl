// ============================================================================
//  GlassEffectPS.hlsl - full liquid glass (ps_5_0, no instruction cap)
//
//  Alpha policy
//  ------------
//  The backdrop texture carries PREMULTIPLIED alpha, because the WPF snapshot
//  may be transparent: with mica / acrylic the window backplate is drawn by
//  DWM, so everything WPF can show us under the lens is "nothing" (alpha = 0).
//
//  The glass therefore must NOT invent an opaque colour of its own (the old
//  version composited the snapshot over a solid #F3F3F3 plate, which turned
//  every lens into a flat grey sticker). Instead:
//
//    * the refracted backdrop keeps its own alpha  -> in mica mode the pill
//      lets the real backdrop through and inherits the surrounding colour;
//    * the glass body adds a thin white pane of its own (SolidOpacity), plus a
//      little extra near the edge (EdgeOpacity) so the capsule still reads as
//      a piece of glass instead of a flat tint.
//
//  Compositing is plain "over" in premultiplied space:
//      aOut  = 1 - (1 - a)(1 - own)
//      rgbOut = col * a * (1 - own) + own        (white body)
//
//  When the backdrop is fully opaque (solid colour / image background) this
//  degenerates to the old behaviour plus a light white veil.
// ============================================================================

Texture2D Frost : register(t0);   // narrow blur (or unblurred for Clear)
Texture2D Fill  : register(t1);   // wider blur / raw backdrop, for Regular
SamplerState LinearSampler : register(s0);

// ---- Constant buffer -------------------------------------------------------
// Packed as explicit float4 on purpose. The previous version declared loose
// scalars ("float Radius; float InvBand; float AAScale;" followed by
// "float2 RefractUV"), and HLSL packed them WITHOUT the padding the CPU side
// assumed - so every parameter from RefractUV onwards landed one float early.
// That silently fed RimStrength = RefractPixels/height (a white rim nobody
// asked for) and shifted FillOpacity / ShadowLift / tones as well.
// Writing seven float4s makes the layout identical on both sides.
cbuffer GlassParams : register(b0)
{
    float4 SizeHalf;   // xy = surface size, zw = half shape
    float4 Shape;      // x = radius, y = invBand, z = aaScale
    float4 Refract;    // xy = refractUV, z = dispersion, w = rimStrength
    float4 Body;       // x = fillOpacity, y = toneWeight, z = shadowLift, w = rimWidth
    float4 Tone;       // x = toneA, y = toneB, z = toneQ, w = tintOpacity
    float4 Tint;       // xyz = tintRGB, w = fresnelPower
    float4 Extra;      // x = innerGlow, y = solidOpacity, z = edgeOpacity, w = coverage
};

#define Size        SizeHalf.xy
#define HalfShape   SizeHalf.zw
#define Radius      Shape.x
#define InvBand     Shape.y
#define AAScale     Shape.z
#define RefractUV   Refract.xy
#define Dispersion  Refract.z
#define RimStrength Refract.w
#define FillOpacity Body.x
#define ToneWeight  Body.y
#define ShadowLift  Body.z
#define RimWidth    Body.w
#define ToneA       Tone.x
#define ToneB       Tone.y
#define ToneQ       Tone.z
#define TintOpacity Tone.w
#define TintRGB     Tint.xyz
#define FresnelPower Tint.w
#define InnerGlow   Extra.x
#define Coverage    Extra.w
#define SolidOpacity Extra.y
#define EdgeOpacity Extra.z

float4 main(float4 svPos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float2 px = uv * Size - Size * 0.5;

    // ---- Squircle SDF ----
    float2 q = abs(px) - HalfShape + Radius;
    float2 c = max(q, 0.0);
    float2 c2 = c * c;
    float radius4 = sqrt(sqrt(c2.x * c2.x + c2.y * c2.y));
    float d = min(max(q.x, q.y), 0.0) + radius4 - Radius;

    // Outward normal
    float2 g = c2 * c * sign(px);
    float2 n = g * rsqrt(max(dot(g, g), 1e-6));

    float mask = saturate(0.5 - d * AAScale);

    // ---- Displacement profile ----
    float depth = saturate(-d * InvBand);
    float fall = 1.0 - depth;
    float edge = fall * fall;

    // Inward displacement
    float2 baseRefr = n * edge * RefractUV;

    // ---- 5-tap refraction sampling (premultiplied accumulate) ----
    float4 acc = 0;
    {
        const int N = 5;
        float totalW = 0;
        [unroll]
        for (int i = 0; i < N; i++)
        {
            float t = (i / (float)(N - 1)) * 2.0 - 1.0;  // -1 .. +1
            float2 dispUV = uv - baseRefr * (1.0 + t * Dispersion);
            float w = 1.0 - abs(t) * 0.4;                  // triangle weight
            acc += Frost.SampleLevel(LinearSampler, dispUV, 0) * w;
            totalW += w;
        }
        acc /= totalW;
    }

    float4 fillP = Fill.SampleLevel(LinearSampler, uv - baseRefr, 0);

    // Back to straight alpha for the colour math below.
    float a = acc.a;
    float3 col = acc.rgb / max(a, 1e-3);
    float3 fillC = fillP.rgb / max(fillP.a, 1e-3);

    // ---- Regular material: mix with wider frost ----
    col = lerp(col, max(col, fillC), FillOpacity);

    // ---- Tone mapping (full cubic curve) ----
    float3 tone = ToneA + col * (ToneB + ToneQ * col);
    col = lerp(col, tone, ToneWeight);

    // Cubic shadow lift
    float3 lift = 1.0 - col;
    col += ShadowLift * lift * lift * lift;

    // ---- Tint (desaturate towards luminance) ----
    float3 tinted = float3(
        dot(col, float3(0.2126, 0.7152, 0.0722)),
        dot(col, float3(0.2126, 0.7152, 0.0722)),
        dot(col, float3(0.2126, 0.7152, 0.0722)));
    tinted = lerp(tinted, tinted * TintRGB, 1.0);
    col = lerp(col, tinted, TintOpacity);

    // ---- Fresnel rim (view-dependent specular) ----
    float band = saturate(1.0 - abs(d) * RimWidth);
    float horiz = n.y * n.y;
    float rim = band * RimStrength;

    float fresnel = pow(saturate(1.0 - abs(d) / max(Radius, 1.0) * 0.5), max(FresnelPower, 0.1));
    float rimTotal = rim * (0.4 + 0.6 * fresnel);

    col += rimTotal * horiz * 0.62;                  // white hairline on top/bottom
    col *= 1.0 - rimTotal * (1.0 - horiz) * 0.34;    // vertical edges in shadow

    // Inner glow: light entering the glass from the top edge
    col += edge * horiz * InnerGlow;

    // ---- Glass body (this is what a mica backdrop sees) ----
    float own = saturate(SolidOpacity + EdgeOpacity * edge);

    // "over" in premultiplied space: refracted backdrop, then white body.
    float aOut = 1.0 - (1.0 - a) * (1.0 - own);
    float3 rgbOut = col * a * (1.0 - own) + own;

    // Coverage: let the glass take over the backdrop (full-window plate).
    // The refracted background is output opaquely, so the sharp wallpaper behind
    // cannot leak through the translucent body and make the glass look fake.
    float cov = saturate(Coverage);
    if (cov > 0.0)
    {
        rgbOut += col * (1.0 - aOut) * cov;
        aOut = 1.0 - (1.0 - aOut) * (1.0 - cov);
    }

    return float4(saturate(rgbOut) * mask, saturate(aOut) * mask);
}
