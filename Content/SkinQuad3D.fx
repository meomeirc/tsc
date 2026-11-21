#if defined(SM6) || defined(VULKAN)
// Native Direct3D 12 uses SM6; DesktopVK compiles HLSL to SPIR-V with Vulkan semantics.
#define VS_SHADERMODEL vs_6_0
#define PS_SHADERMODEL ps_6_0
// Shader Model 6 requires a system-value render-target semantic. COLOR0 is
// retained for legacy DirectX and OpenGL effect profiles.
#define PS_OUTPUT_SEMANTIC SV_Target0
#elif OPENGL
#define PS_OUTPUT_SEMANTIC COLOR0
#if TSC_ANDROID
// Keep the Android GLES shader profile's SV_POSITION compatibility mapping
// isolated from DesktopGL, where the native SV_POSITION semantic is required.
#define SV_POSITION POSITION
#endif
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define PS_OUTPUT_SEMANTIC COLOR0
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

#if defined(SM6) || defined(VULKAN)
Texture2D Texture : register(t0);
Texture2D MaskTexture : register(t1);
SamplerState TextureSampler : register(s0);
SamplerState MaskTextureSampler : register(s1);
#define SAMPLE_TEXTURE(textureName, samplerName, uv) textureName.Sample(samplerName, uv)
#else
texture Texture;
texture MaskTexture;
#define SAMPLE_TEXTURE(textureName, samplerName, uv) tex2D(samplerName, uv)
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

#if !defined(SM6) && !defined(VULKAN)
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
#endif

#if SM6
// Native SM6 backends use the compact VertexPositionTexture input signature.
// DX12 needs this to avoid its extra-attribute pipeline issue; Vulkan uses the
// same declaration so the renderer can supply tint and mask UV as constants.
float4 VertexColor;
float4 MaskUv;
float4 TextureUvRect;
struct VSInput
{
    float3 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};
#else
struct VSInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord     : TEXCOORD0;
    float2 MaskTexCoord : TEXCOORD1;
};
#endif

struct VSOutput
{
    // Keep this declaration order stable. DX12 links the pixel shader by the
    // signature register assignments produced from this output structure.
    float4 Position     : SV_POSITION;
    float4 Color        : COLOR0;
    float2 TexCoord     : TEXCOORD0;
    float2 MaskTexCoord : TEXCOORD1;
#if !SM6
    // Legacy profiles carry clip position through a normal interpolant because
    // their pixel shader cannot consume SV_POSITION as an input varying.
    float4 ScreenPosition : TEXCOORD2;
#endif
};

struct PSInput
{
#if SM6
    // Match the VS output signature order exactly. The prior DX12 layout
    // omitted SV_POSITION and began with COLOR0, shifting every varying to a
    // different hardware register and causing CreateGraphicsPipelineState
    // shader-linkage errors (including the same failure seen in LaneLine.fx).
    float4 FragmentPosition : SV_POSITION;
    float4 Color             : COLOR0;
    float2 TexCoord          : TEXCOORD0;
    float2 MaskTexCoord      : TEXCOORD1;
#else
    float4 Color          : COLOR0;
    float2 TexCoord       : TEXCOORD0;
    float2 MaskTexCoord   : TEXCOORD1;
    float4 ScreenPosition : TEXCOORD2;
#endif
};

VSOutput MainVS(VSInput input)
{
    VSOutput output;
#if SM6
    output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
    output.Color = VertexColor;
    float2 uvExtent = TextureUvRect.zw - TextureUvRect.xy;
    float2 quadUv = (input.TexCoord - TextureUvRect.xy) /
                    float2(abs(uvExtent.x) > 0.000001f ? uvExtent.x : 1.0f,
                           abs(uvExtent.y) > 0.000001f ? uvExtent.y : 1.0f);
    output.MaskTexCoord = lerp(MaskUv.xy, MaskUv.zw, saturate(quadUv));
#else
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color = input.Color;
    output.MaskTexCoord = input.MaskTexCoord;
#endif
#if !SM6
    output.ScreenPosition = output.Position;
#endif
    output.TexCoord = input.TexCoord;
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

float4 MainPS(PSInput input) : PS_OUTPUT_SEMANTIC
{
#if SM6
    // DX12 supplies SV_POSITION to the pixel shader in render-target pixel
    // coordinates. This is the exact fragment position, so no extra clip-space
    // varying or reconstruction is needed for clipping/bilinear UV mapping.
    float2 screenPosition = input.FragmentPosition.xy;
#else
    float2 screenPosition = GetScreenPixelPosition(input.ScreenPosition);
#endif

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
    float4 texel = SAMPLE_TEXTURE(Texture, TextureSampler, uv);

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
            ? SAMPLE_TEXTURE(MaskTexture, MaskTextureSampler, maskUv).a
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
