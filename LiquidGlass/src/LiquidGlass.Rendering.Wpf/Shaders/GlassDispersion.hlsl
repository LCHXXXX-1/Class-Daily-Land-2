// ============================================================================
//  GlassDispersion.hlsl - chromatic dispersion (per-channel refraction)
//
//  Different wavelengths have different refractive indices, so a glass rim
//  splits white light into color fringes. We sample R/G/B with slightly
//  different refraction scales instead of faking a rainbow gradient.
//  Keep the strength tiny - above ~0.15 it reads as a cheap candy outline.
// ============================================================================

// Scale the refraction offset per channel. The base direction is shared,
// only the magnitude differs.
float2 DisperseUV(float2 uv, float2 offset, float channelScale)
{
    return uv + offset * channelScale;
}
