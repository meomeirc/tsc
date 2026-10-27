#if OPENGL
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
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VSOutput MainVS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VSOutput input) : COLOR0
{
    // Map U from the actual projected left/right edges of the single quad.
    // This avoids assuming those edges are perpendicular to the centerline.
    // The lane-line geometry already contains the correct 3D surface shape;
    // the shader only asks where the fragment sits between the two real edges.
    float2 centerDirection = ((LineNearLeft + LineNearRight) * 0.5f) -
                             ((LineFarLeft + LineFarRight) * 0.5f);
    float centerLengthSq = dot(centerDirection, centerDirection);
    float t = 0.0f;
    if (centerLengthSq > 0.000001f)
    {
        t = saturate(dot(
            input.Position.xy - (LineFarLeft + LineFarRight) * 0.5f,
            centerDirection) / centerLengthSq);
    }

    float2 leftEdge = lerp(LineFarLeft, LineNearLeft, t);
    float2 rightEdge = lerp(LineFarRight, LineNearRight, t);
    float2 across = rightEdge - leftEdge;
    float acrossLengthSq = dot(across, across);

    float u = 0.5f;
    if (acrossLengthSq > 0.000001f)
        u = dot(input.Position.xy - leftEdge, across) / acrossLengthSq;

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
