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
SamplerState TextureSampler : register(s0);
#define SAMPLE_TEXTURE(textureName, samplerName, uv) textureName.Sample(samplerName, uv)
#else
texture Texture;
sampler TextureSampler = sampler_state
{
    Texture = <Texture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};
#define SAMPLE_TEXTURE(textureName, samplerName, uv) tex2D(samplerName, uv)
#endif

float4x4 WorldViewProjection;
float2 LineFarLeft;
float2 LineFarRight;
float2 LineNearLeft;
float2 LineNearRight;
float2 ViewportSize;
float2 ViewportOrigin;
float ScreenHeight;

#if SM6
// DX12 uses the compact VertexPositionTexture layout. Tint is uniform for the
// six vertices of a lane-line command and is supplied as a shader parameter.
float4 VertexColor;
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
    float2 TexCoord : TEXCOORD0;
};
#endif

struct VSOutput
{
    float4 Position       : SV_POSITION;
    float4 Color          : COLOR0;
    float2 TexCoord       : TEXCOORD0;
#if !SM6
    // OpenGL ps_3_0 cannot read SV_POSITION as a pixel input, so carry clip
    // position through a normal varying on that shader path only.
    float4 ScreenPosition : TEXCOORD1;
#endif
};

struct PSInput
{
#if SM6
    // Direct3D links adjacent shader stages by signature register order, not
    // semantic name alone. Keep this order identical to VSOutput: SV_POSITION,
    // COLOR0, then TEXCOORD0. Reordering these fields makes the DX12 PSO
    // signatures incompatible even though all three semantic names match.
    float4 FragmentPosition : SV_POSITION;
    float4 Color             : COLOR0;
    float2 TexCoord          : TEXCOORD0;
#else
    float4 Color          : COLOR0;
    float2 TexCoord       : TEXCOORD0;
    float4 ScreenPosition : TEXCOORD1;
#endif
};
VSOutput MainVS(VSInput input)
{
    VSOutput output;
#if SM6
    output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
    output.Color = VertexColor;
#else
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color = input.Color;
#endif
#if !SM6
    output.ScreenPosition = output.Position;
#endif
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(PSInput input) : PS_OUTPUT_SEMANTIC
{
    float2 fragmentPosition;
#if SM6
    // SV_POSITION is in render-target pixel coordinates; convert it to the
    // viewport-local, top-down coordinates used by the four projected corners.
    fragmentPosition = input.FragmentPosition.xy - ViewportOrigin;
#else
    // For OpenGL ps_3_0, reconstruct viewport-local pixels from the carried
    // clip-space position. Dividing its perspective-correct interpolation by W
    // gives the actual fragment NDC coordinates without requiring VPOS.
    float4 clipPosition = input.ScreenPosition;
    float safeW = max(abs(clipPosition.w), 0.00001f) *
                  (clipPosition.w < 0.0f ? -1.0f : 1.0f);
    float2 ndc = clipPosition.xy / safeW;
    fragmentPosition = float2(
        (ndc.x * 0.5f + 0.5f) * ViewportSize.x,
        (0.5f - ndc.y * 0.5f) * ViewportSize.y);
#endif
    float2 farLeft = LineFarLeft;
    float2 farRight = LineFarRight;
    float2 nearLeft = LineNearLeft;
    float2 nearRight = LineNearRight;
#if TSC_ANDROID
    // MojoShader's GLES fragment shaders use mediump by default. Absolute
    // 1080p/4K pixel coordinates overflow its reliable range during dot
    // products, which makes the lane texture's interpolation visibly skew.
    // Work in viewport-height units instead (coordinates stay close to 0..2).
    float inverseScreenHeight = rcp(max(ScreenHeight, 1.0f));
    fragmentPosition *= inverseScreenHeight;
    farLeft *= inverseScreenHeight;
    farRight *= inverseScreenHeight;
    nearLeft *= inverseScreenHeight;
    nearRight *= inverseScreenHeight;
#endif

    // Map U from the actual projected left/right edges of the single quad.
    // This avoids assuming those edges are perpendicular to the centerline.
    // The lane-line geometry already contains the correct 3D surface shape;
    // the shader only asks where the fragment sits between the two real edges.
    float2 centerDirection = ((nearLeft + nearRight) * 0.5f) -
                             ((farLeft + farRight) * 0.5f);
    float centerLengthSq = dot(centerDirection, centerDirection);
    float t = 0.0f;
    if (centerLengthSq > 0.000001f)
    {
        t = saturate(dot(
            fragmentPosition - (farLeft + farRight) * 0.5f,
            centerDirection) / centerLengthSq);
    }

    float2 leftEdge = lerp(farLeft, nearLeft, t);
    float2 rightEdge = lerp(farRight, nearRight, t);
    float2 across = rightEdge - leftEdge;
    float acrossLengthSq = dot(across, across);

    float u = 0.5f;
    if (acrossLengthSq > 0.000001f)
        u = dot(fragmentPosition - leftEdge, across) / acrossLengthSq;

    float2 uv = float2(saturate(u), input.TexCoord.y);
    return SAMPLE_TEXTURE(Texture, TextureSampler, uv) * input.Color;
}

technique LaneLine
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
