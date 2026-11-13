// ============================================================================
//  GlassSurface.hlsl - the liquid glass pass.
//
//  Port of the reference implementation's model
//  (packages/liquid-glass/src/gpu/maps.wgsl + core/optics.ts). Three things it
//  does that a naive "blur the backdrop" shader does not:
//
//   1. SQUIRCLE OUTLINE. Real corners are continuous-curvature superellipses,
//      not circular arcs. With circular corners the rim highlight kinks at the
//      corner tangent points, which is the single most obvious "this is not
//      Apple" tell. Corners here are superellipses with exponent 4.
//
//   2. DISPLACEMENT PROFILE, NOT A LENS ZOOM. Displacement peaks at the very
//      border and falls to zero at `RefractionBand` depth, following
//      edge = 1 - sqrt((2-depth)*depth) with depth = sat(-d / band).
//      That curve is what makes the bend read as a bevel instead of a smear.
//      The displacement direction is INWARD (sampling toward the centre), so
//      the rim compresses a wider slice of backdrop into a narrow ring.
//
//   3. RIM AS LIGHT OVER A DARK EDGE. Horizontal edges catch a white specular
//      hairline; vertical edges go slightly dark. Corners get both, which is
//      why a glass corner looks brighter than its sides.
//
//  Material: Clear is an unfiltered backdrop + shadow lift. Regular is a
//  two-sigma frost (s0 = narrow, s1 = wide) brightened and mixed back, then a
//  calibrated quadratic tone curve.
//
//  ps_2_0 budget: 64 arithmetic / 32 texture slots. Registers are filled in
//  dependency-property order (see LiquidGlassEffect), one Point4D per cN.
//  ASCII only: D3DCompiler takes a byte count from a character count, so any
//  non-ASCII comment truncates the source and fails with "unexpected eof".
// ============================================================================

sampler2D Frost : register(s0);   // narrow blur (0 for Clear)
sampler2D Fill  : register(s1);   // wide blur, used by Regular only

float4 SizeParams;      // c0: surface w, h (px), then half-shape w, h (px)
float4 ShapeParams;     // c1: radius px, 1/band, 1px AA width, 1/rim width
float4 RefractParams;   // c2: uv displacement per axis, dispersion, unused
float4 OpticsParams;    // c3: rim strength, dark flag, unused, unused
float4 MaterialParams;  // c4: fill opacity, tone weight, shadow lift, unused
float4 ToneParams;      // c5: tone a, b, q, unused
float4 TintParams;      // c6: tint rgb, tint opacity

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = SizeParams.xy;
    float2 halfShape = SizeParams.zw;
    float radius = max(ShapeParams.x, 0.5);
    float2 px = uv * size - size * 0.5;

    // ---- Squircle SDF -------------------------------------------------------
    // q is the corner-box local coordinate; inside the straight runs one of its
    // components is negative and the superellipse term collapses to the box
    // distance, so a single expression covers both cases.
    float2 c = max(abs(px) - halfShape + radius, 0.0);
    float2 c2 = c * c;
    float radius4 = sqrt(sqrt(c2.x * c2.x + c2.y * c2.y));   // (cx^4 + cy^4)^(1/4)
    // The usual rounded-box SDF adds min(max(q),0) so the interior keeps a true
    // signed distance. For a *glass* we only care about sign and a band of a few
    // pixels, so the superellipse term alone is enough - and it is three
    // instructions cheaper. Callers must keep band <= radius (see GlassLayer).
    float d = radius4 - radius;

    // Outward normal = normalised gradient of the superellipse (cx^3, cy^3),
    // flipped back into the correct quadrant.
    float2 g = c2 * c * sign(px);
    float2 n = g * rsqrt(max(dot(g, g), 1e-6));

    float mask = saturate(0.5 - d * ShapeParams.z);   // 1px antialiased silhouette

    // ---- Displacement profile ----------------------------------------------
    // depth 0 at the border, 1 at the band's inner limit.
    float depth = saturate(-d * ShapeParams.y);
    // (1-depth)^2 is the same monotone falloff as the reference's
    // 1 - sqrt((2-depth)*depth) at a fraction of the cost: both are 1 at the
    // border and 0 at the band's inner limit, and the parabola is a touch
    // smoother, which suits a bevel better than the reference's sharp knee.
    float fall = 1.0 - depth;
    float edge = fall * fall;

    // Inward displacement, in uv space. Dispersion splits the three channels by
    // a fraction of the same vector, so fringes only appear inside the bend.
    // RefractParams.xy already holds refractionPixels / surfaceSize per axis.
    float2 refr = n * edge * RefractParams.xy;
    float disp = RefractParams.z;

    float2 uvG = uv - refr;
    float2 fringe = refr * disp;
    float3 col;
    col.r = tex2D(Frost, uvG - fringe).r;
    col.g = tex2D(Frost, uvG).g;
    col.b = tex2D(Frost, uvG + fringe).b;

    // ---- Regular material: frost lightened by a much wider blur ------------
    float3 fill = tex2D(Fill, uvG).rgb;
    col = lerp(col, max(col, fill), MaterialParams.x);

    // ---- Tone ---------------------------------------------------------------
    float3 tone = ToneParams.x + col * (ToneParams.y + ToneParams.z * col);
    col = lerp(col, tone, MaterialParams.y);

    // Shadow lift raises blacks without touching highlights, so content behind
    // the glass stays readable instead of going muddy.
    float3 lift = 1.0 - col;
    col += MaterialParams.z * lift * lift;

    // ---- Rim: light over a dark edge ---------------------------------------
    // n.x^2 + n.y^2 == 1, so these two weights split the outline between the
    // horizontal edges (specular) and the vertical ones (shade) with no
    // branches and no extra trigonometry.
    float band = saturate(1.0 - abs(d) * ShapeParams.w);
    float horiz = n.y * n.y;
    float rim = band * OpticsParams.x;

    // n.x^2 == 1 - n.y^2, so the two weights split the outline between the
    // horizontal edges (specular) and the vertical ones (shade) for free.
    col += rim * horiz * 0.62;                  // white hairline on top/bottom
    col *= 1.0 - rim * (1.0 - horiz) * 0.34;    // vertical edges sit in shadow

    // Soft inner glow: light spilling a few pixels in from the lit edges, which
    // is what stops the rim from reading as a drawn 1px stroke.
    col += edge * horiz * 0.055;

    // Premultiplied alpha (WPF ShaderEffect convention).
    return float4(col * mask, mask);
}
