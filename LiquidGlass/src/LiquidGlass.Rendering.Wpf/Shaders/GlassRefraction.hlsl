// ============================================================================
//  GlassRefraction.hlsl - rounded-rect SDF, edge weight, refraction normal
//
//  Mirror of LiquidGlass.Core/GlassGeometry.cs. The C# version drives hit
//  testing and shadow outlines; this one drives per-pixel refraction. Both must
//  stay identical or the visual edge drifts away from the clickable edge.
// ============================================================================

// Signed distance to a rounded rect (negative inside). Unit: pixels.
float SdRoundedRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// Outward normal approximation: axis-aligned on the straight segments,
// radial on the corner arcs. ps_2_0 has no ddx/ddy, so this is analytic.
float2 EdgeNormal(float2 p, float2 q)
{
    float2 dir;

    if (q.x > 0.0 && q.y > 0.0)
    {
        // Corner arc: radial direction from the arc center.
        dir = normalize(q) * sign(p);
    }
    else if (q.x > q.y)
    {
        dir = float2(sign(p.x), 0.0);
    }
    else
    {
        dir = float2(0.0, sign(p.y));
    }

    // normalize(0) is undefined; bias away from the exact center.
    return normalize(dir + float2(0.0001, 0.0001));
}

// Edge weight: 0 at the center, 1 on the boundary.
// band is the transition width - too narrow leaves a hard ring on the edge,
// too wide bends the whole control instead of just its rim.
float EdgeWeight(float d, float band)
{
    return saturate(1.0 + d / max(band, 0.001));
}
