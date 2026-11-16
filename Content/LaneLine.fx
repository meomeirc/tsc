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
texture Texture;
float2 LineFarLeft;
float2 LineFarRight;
float2 LineNearLeft;
float2 LineNearRight;
float2 ViewportSize;
float ScreenHeight;

sampler TextureSampler = sampler_state
{
    Texture = <Texture>;
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
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position       : SV_POSITION;
    float4 Color          : COLOR0;
    float2 TexCoord       : TEXCOORD0;
    float4 ScreenPosition : TEXCOORD1;
};

// Carry clip-space position as an ordinary varying. POSITION/SV_POSITION is
// reserved for the rasterizer on OpenGL ps_3_0 and cannot be read as a pixel
// shader input. Dividing the perspective-correct interpolated clip position
// by W reconstructs the fragment's exact NDC position without VPOS.
struct PSInput
{
    float4 Color          : COLOR0;
    float2 TexCoord       : TEXCOORD0;
    float4 ScreenPosition : TEXCOORD1;
};

VSOutput MainVS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.ScreenPosition = output.Position;
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(PSInput input) : COLOR0
{
    // Reconstruct viewport-local top-down pixel coordinates from clip space.
    // This avoids the unsupported POSITION/VPOS pixel input semantic and works
    // identically on OpenGL and DirectX, including a centered 16:9 viewport.
    float4 clipPosition = input.ScreenPosition;
    float safeW = max(abs(clipPosition.w), 0.00001f) *
                  (clipPosition.w < 0.0f ? -1.0f : 1.0f);
    float2 ndc = clipPosition.xy / safeW;
    float2 fragmentPosition = float2(
        (ndc.x * 0.5f + 0.5f) * ViewportSize.x,
        (0.5f - ndc.y * 0.5f) * ViewportSize.y);
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
    return tex2D(TextureSampler, uv) * input.Color;
}

technique LaneLine
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
