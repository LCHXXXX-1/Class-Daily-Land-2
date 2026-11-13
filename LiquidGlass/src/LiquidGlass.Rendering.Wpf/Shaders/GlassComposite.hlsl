// ============================================================================
//  GlassComposite.hlsl - glass pass, modelled on the proven displacement-map
//  recipe (Shu Ding's liquid-glass, and the Aave-style SVG displacement maps).
//
//  The insight those implementations get right, which the first version of this
//  shader got wrong:
//
//    * The INTERIOR must be an identity mapping. Whatever softening the backdrop
//      already has, the panel must not add more - otherwise it reads as frosted
//      plastic. That is why the blurred version looked like a mush.
//    * Refraction lives ONLY in a narrow band beside the border, and it works by
//      REMAPPING the sample coordinate (a lens), not by blurring.
//    * The remap is a smoothstep of the SDF, so the bend eases in and out. A
//      linear ramp (or a high power like edge^3) leaves either a visible ring or
//      almost nothing.
//    * Dispersion = the same remap evaluated at slightly different strengths for
//      R / G / B, so fringes appear only where the bend is non-zero.
//
//  The sampler holds the backdrop crop, and the crop is padded on every side
//  (GlassLayer.Expand), so sampling slightly beyond the widget edge returns real
//  neighbouring content instead of a clamped smear.
//
//  ps_2_0 budget: 64 arithmetic / 32 texture slots.
//  NOTE: ASCII-only. D3DCompiler gets a byte count derived from character
//  count, so non-ASCII comments truncate the source mid-statement.
// ============================================================================

sampler2D InputTexture : register(s0);

float4 SizeParams;    // CropSize.xy (px), unused, RadiusPx
float4 ShapeParams;   // WidgetHalfSize.xy (px), InvBand, EdgeBandPx
float4 OpticsParams;  // LensAmp, Dispersion, RimIntensity, unused
float4 TintParams;    // TintColor.rgb, TintOpacity
float4 LightParams;   // LightDir.xy (unit), LightIntensity, EdgeThickness

// Smooth 0..1 ramp used to ease the lens in. Matches smoothStep() in the
// reference implementations.
float SmoothStep01(float t)
{
    t = saturate(t);
    return t * t * (3.0 - 2.0 * t);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 cropSize = SizeParams.xy;
    float2 halfSize = ShapeParams.xy;
    float radius = SizeParams.w;

    // Widget-local pixels, origin at the widget center (crop is centred on it).
    float2 px = uv * cropSize - cropSize * 0.5;

    // ---- Rounded-rect SDF, negative inside the widget ----
    float2 q = abs(px) - halfSize + radius;
    float2 outside = max(q, 0.0);
    float d = length(outside) + min(max(q.x, q.y), 0.0) - radius;

    // Antialiased silhouette.
    float mask = saturate(-d);

    // Outward normal: radial on the corner arcs, axis-aligned on the runs.
    float2 normal = normalize(outside * sign(px) + float2(0.0002, 0.0002));

    // ---- Edge weight: 0 in the interior, 1 on the border ----
    float edge = saturate(1.0 + d * ShapeParams.z);
    float edge2 = edge * edge;

    // ---- Lens remap ----
    // Sample coordinates are pulled INWARD near the border (factor below 1), so
    // the rim squeezes a wider slice of the backdrop into a narrow ring. That
    // compression is the refraction the eye picks up. Amplitude is a fraction of
    // the half size (see GlassLayer), i.e. only a few percent - the reference
    // implementations use ~5-12%; anything near 0.5 smears the whole panel.
    float eased = SmoothStep01(edge);
    float lens = 1.0 - OpticsParams.x * eased;

    // ---- Dispersion: same remap at slightly different strengths per channel,
    //      so colour fringes appear only across the bent band. ----
    float disp = OpticsParams.y * edge2;
    float2 center = float2(0.5, 0.5);

    float2 uvR = center + (uv - center) * (lens * (1.0 + disp));
    float2 uvG = center + (uv - center) * lens;
    float2 uvB = center + (uv - center) * (lens * (1.0 - disp));

    float3 color;
    color.r = tex2D(InputTexture, uvR).r;
    color.g = tex2D(InputTexture, uvG).g;
    color.b = tex2D(InputTexture, uvB).b;

    // ---- Tint film ----
    color = lerp(color, TintParams.rgb, TintParams.w);

    // ---- Rim light ----
    // iOS-like borders are BRIGHT, not a dark ring: a crisp specular hairline on
    // the very edge, plus a softer lobe facing the light. The hairline keeps the
    // silhouette readable even over a dark backdrop.
    // NOTE: the light must stay on the actual border ring. Using `eased` there
    // spreads white across the whole bend band and washes the panel out - that
    // is why the rim terms are powered WAY up (edge^6 / edge^4).
    float facing = saturate(dot(normal, LightParams.xy) * 0.5 + 0.5);
    float e4 = edge2 * edge2;
    float e6 = e4 * edge2;
    float hairline = e6 * 0.85;                            // crisp, always on
    float lobe = e4 * (0.10 + 0.90 * facing);              // light-driven
    color += (hairline + lobe) * OpticsParams.z * LightParams.z * 0.75;

    // ---- Bevel: only a whisper of darkening, ~1/3 in from the rim. A stronger
    //      ring reads as cheap plastic instead of polished glass. ----
    float bevel = eased * (1.0 - eased);
    color *= 1.0 - bevel * LightParams.w * 0.22;

    // Premultiplied alpha (WPF ShaderEffect convention).
    return float4(color * mask, mask);
}
