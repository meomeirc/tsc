#if OPENGL
#if TSC_ANDROID
// Keep the Android GLES shader profile's SV_POSITION compatibility mapping
// isolated from DesktopGL, where the native SV_POSITION semantic is required.
#define SV_POSITION POSITION
#endif
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 WorldViewProjection;
float2 ScreenTopLeft;
float2 ScreenTopRight;
float2 ScreenBottomRight;
float2 ScreenBottomLeft;
float3 DiffuseColor;
float Alpha;
float MaskEnabled;
float ForceWhiteColor;
float UseBilinearMapping;
float ClipEnabled;
float4 ClipRect;
float ScreenHeight;
float2 ViewportSize;
float2 ViewportOrigin;
texture Texture;
texture MaskTexture;

sampler TextureSampler = sampler_state
{
    Texture = <Texture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};

sampler MaskTextureSampler = sampler_state
{
    Texture = <MaskTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};

struct VSInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord     : TEXCOORD0;
    float2 MaskTexCoord : TEXCOORD1;
};

struct VSOutput
{
    float4 Position       : SV_POSITION;
    float4 Color          : COLOR0;
    float2 TexCoord       : TEXCOORD0;
    float2 MaskTexCoord   : TEXCOORD1;
    float4 ScreenPosition : TEXCOORD2;
};

// Keep clip-space position in a normal interpolant. The rasterizer owns the
// SV_POSITION semantic; attempting to consume it as a ps_3_0 input produces
// X4502 on OpenGL. Perspective-correct interpolation followed by XY/W gives
// the actual fragment position in NDC without a VPOS dependency.
struct PSInput
{
    float4 Color          : COLOR0;
    float2 TexCoord       : TEXCOORD0;
    float2 MaskTexCoord   : TEXCOORD1;
    float4 ScreenPosition : TEXCOORD2;
};

VSOutput MainVS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.ScreenPosition = output.Position;
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    output.MaskTexCoord = input.MaskTexCoord;
    return output;
}

float2 GetScreenPixelPosition(float4 clipPosition)
{
    float safeW = max(abs(clipPosition.w), 0.00001f) *
                  (clipPosition.w < 0.0f ? -1.0f : 1.0f);
    float2 ndc = clipPosition.xy / safeW;
    float2 fragmentPosition = ViewportOrigin + float2(
        (ndc.x * 0.5f + 0.5f) * ViewportSize.x,
        (0.5f - ndc.y * 0.5f) * ViewportSize.y);
#if TSC_ANDROID
    // Keep screen-space calculations in a small normalized range. MojoShader's
    // GLES pixel shaders use mediump precision, which cannot safely square or
    // dot raw 1080p/4K pixel coordinates.
    return fragmentPosition * rcp(max(ScreenHeight, 1.0f));
#else
    return fragmentPosition;
#endif
}

float2 GetQuadScreenPosition(float2 screenPosition)
{
#if TSC_ANDROID
    return screenPosition * rcp(max(ScreenHeight, 1.0f));
#else
    return screenPosition;
#endif
}

float2 BilinearQuadUv(float2 screenPosition)
{
    float2 topLeft = GetQuadScreenPosition(ScreenTopLeft);
    float2 topRight = GetQuadScreenPosition(ScreenTopRight);
    float2 bottomRight = GetQuadScreenPosition(ScreenBottomRight);
    float2 bottomLeft = GetQuadScreenPosition(ScreenBottomLeft);
    float2 topCenter = (topLeft + topRight) * 0.5f;
    float2 bottomCenter = (bottomLeft + bottomRight) * 0.5f;
    float2 down = bottomCenter - topCenter;
    float downLengthSq = dot(down, down);

    float v = 0.0f;
    if (downLengthSq > 0.000001f)
    {
        v = saturate(dot(screenPosition - topCenter, down) / downLengthSq);
    }

    float2 leftEdge = lerp(topLeft, bottomLeft, v);
    float2 rightEdge = lerp(topRight, bottomRight, v);
    float2 across = rightEdge - leftEdge;
    float acrossLengthSq = dot(across, across);

    float u = 0.5f;
    if (acrossLengthSq > 0.000001f)
    {
        u = saturate(dot(screenPosition - leftEdge, across) / acrossLengthSq);
    }

    return float2(u, v);
}

float4 MainPS(PSInput input) : COLOR0
{
    float2 screenPosition = GetScreenPixelPosition(input.ScreenPosition);

    // ClipRect is expressed in screen pixels, so the 3D quad keeps its original
    // projected geometry and texture UVs while the visible pixels are wiped.
    if (ClipEnabled > 0.5f)
    {
        // Lua clip bounds are local to the game viewport, while projected
        // corners above include the physical viewport origin. Remove that
        // origin only for clipping so wide-screen letterboxing cannot shift it.
#if TSC_ANDROID
        float inverseScreenHeight = rcp(max(ScreenHeight, 1.0f));
        float2 clipPosition = screenPosition - ViewportOrigin * inverseScreenHeight;
        float4 clipRect = ClipRect * inverseScreenHeight;
#else
        float2 clipPosition = screenPosition - ViewportOrigin;
        float4 clipRect = ClipRect;
#endif
        float insideDistance = min(
            min(clipPosition.x - clipRect.x, clipRect.z - clipPosition.x),
            min(clipPosition.y - clipRect.y, clipRect.w - clipPosition.y));
        clip(insideDistance);
    }

    float2 uv = UseBilinearMapping > 0.5f
        ? BilinearQuadUv(screenPosition)
        : input.TexCoord;
    float4 texel = tex2D(TextureSampler, uv);

    // Optional flash treatment: preserve the source alpha as a silhouette,
    // but output white RGB so the overlay can use ordinary alpha blending
    // instead of additive blending.
    if (ForceWhiteColor > 0.5f)
    {
        texel.rgb = float3(1.0f, 1.0f, 1.0f);
    }

    if (MaskEnabled > 0.5f)
    {
        float2 maskUv = input.MaskTexCoord;
        float maskInside = (maskUv.x >= 0.0f && maskUv.x <= 1.0f &&
                            maskUv.y >= 0.0f && maskUv.y <= 1.0f) ? 1.0f : 0.0f;
        float maskAlpha = maskInside > 0.5f
            ? tex2D(MaskTextureSampler, maskUv).a
            : 0.0f;
        texel.a *= maskAlpha;
    }

    float4 tint = float4(DiffuseColor, Alpha);
    return texel * input.Color * tint;
}

technique SkinQuad3D
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
