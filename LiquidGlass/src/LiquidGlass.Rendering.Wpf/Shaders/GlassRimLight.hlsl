// ============================================================================
//  GlassRimLight.hlsl - rim light and ambient sheen
//
//  The lit rim is what separates glass from plastic:
//    1) the half facing the light source is brighter (lambert facing term)
//    2) the highlight band is narrow (pow) and sits just inside the edge,
//       which reads as thickness
//    3) the center is untouched
// ============================================================================

// edge: edge weight, normal: outward normal, lightDir: direction to the light.
float RimLight(float edge, float2 normal, float2 lightDir, float power, float intensity)
{
    float facing = saturate(dot(normal, lightDir) * 0.5 + 0.5);
    float band = pow(edge, power);
    return band * (0.35 + 0.65 * facing) * intensity;
}

// Ambient sheen: a faint top glow fixed to screen-up. This layer is
// "environment", not direct light, so it does not follow the light angle.
float AmbientSheen(float2 uv, float intensity)
{
    float top = saturate(1.0 - uv.y * 2.6);   // only the top ~40%
    return top * top * intensity;
}
