#if OPENGL
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
float UseBilinearMapping;
float ClipEnabled;
float4 ClipRect;
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
    float4 Position     : SV_POSITION;
    float4 Color        : COLOR0;
    float2 TexCoord     : TEXCOORD0;
    float2 MaskTexCoord : TEXCOORD1;
};

VSOutput MainVS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    output.MaskTexCoord = input.MaskTexCoord;
    return output;
}

float2 BilinearQuadUv(float2 screenPosition)
{
    float2 topCenter = (ScreenTopLeft + ScreenTopRight) * 0.5f;
    float2 bottomCenter = (ScreenBottomLeft + ScreenBottomRight) * 0.5f;
    float2 down = bottomCenter - topCenter;
    float downLengthSq = dot(down, down);

    float v = 0.0f;
    if (downLengthSq > 0.000001f)
    {
        v = saturate(dot(screenPosition - topCenter, down) / downLengthSq);
    }

    float2 leftEdge = lerp(ScreenTopLeft, ScreenBottomLeft, v);
    float2 rightEdge = lerp(ScreenTopRight, ScreenBottomRight, v);
    float2 across = rightEdge - leftEdge;
    float acrossLengthSq = dot(across, across);

    float u = 0.5f;
    if (acrossLengthSq > 0.000001f)
    {
        u = saturate(dot(screenPosition - leftEdge, across) / acrossLengthSq);
    }

    return float2(u, v);
}

float4 MainPS(VSOutput input) : COLOR0
{
    // ClipRect is expressed in screen pixels, so the 3D quad keeps its original
    // projected geometry and texture UVs while the visible pixels are wiped.
    if (ClipEnabled > 0.5f)
    {
        float2 screenPosition = input.Position.xy;
        float insideDistance = min(
            min(screenPosition.x - ClipRect.x, ClipRect.z - screenPosition.x),
            min(screenPosition.y - ClipRect.y, ClipRect.w - screenPosition.y));
        clip(insideDistance);
    }

    float2 uv = UseBilinearMapping > 0.5f
        ? BilinearQuadUv(input.Position.xy)
        : input.TexCoord;
    float4 texel = tex2D(TextureSampler, uv);

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
