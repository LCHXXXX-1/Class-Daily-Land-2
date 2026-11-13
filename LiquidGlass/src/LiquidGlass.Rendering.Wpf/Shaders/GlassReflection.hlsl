// ============================================================================
//  GlassReflection.hlsl - environment reflection
//
//  A glass surface mirrors the "upper environment" (sky / room light), which is
//  what makes it feel transparent instead of plastic. We cannot get a real
//  cubemap inside WPF, so this is a soft gradient along the light direction:
//  brightest on the edge facing the light, decaying rapidly toward the center.
// ============================================================================

// edge: edge weight, uv: normalized widget coords, lightDir: direction to light.
float EnvironmentReflection(float edge, float2 uv, float2 lightDir, float intensity)
{
    // diag: position along the axis from center toward the light source
    float diag = dot(uv - 0.5, normalize(lightDir + float2(0.0001, 0.0001))) + 0.5;
    diag = saturate(diag);

    // Only near the lit edge; fades to zero at the center.
    float refl = pow(edge, 2.5) * pow(diag, 2.0);
    return refl * intensity;
}
