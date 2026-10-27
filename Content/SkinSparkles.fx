#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 WorldViewProjection;
texture Texture;
float Time;
float2 GridSize;
float Density;
float MinSize;
float MaxSize;
float CycleMin;
float CycleMax;
float FlashWidthMin;
float FlashWidthMax;
float Brightness;
float Sharpness;
float BurstChance;
float MinJump;
float MaxJump;
float DistributionBias;
float RandomSeed;
float TextureCount;
float TextureIndex;
float QuadAspect;
float TextureAspect;

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

float Hash(float n)
{
    return frac(sin(n * 12.9898f + 78.233f) * 43758.5453f);
}

float HashCell(float2 cell, float salt)
{
    return Hash(cell.x * 127.1f + cell.y * 311.7f + salt + RandomSeed * 17.13f);
}

float2 HashCell2(float2 cell, float salt)
{
    return float2(
        HashCell(cell, salt + 13.17f),
        HashCell(cell, salt + 71.91f));
}

float4 MainPS(VSOutput input) : COLOR0
{
    float2 uv = saturate(input.TexCoord);
    float2 baseCell = floor(uv * GridSize);
    float4 result = float4(0, 0, 0, 0);

    // The grid is only a placement lattice. A sparkle is allowed to move and
    // render freely across cell boundaries; neighboring cells are searched so
    // the grid never acts as a clipping boundary.
    for (int oy = -2; oy <= 2; oy++)
    {
        for (int ox = -2; ox <= 2; ox++)
        {
            float2 cell = baseCell + float2((float)ox, (float)oy);

            if (cell.x < 0.0f || cell.y < 0.0f ||
                cell.x >= GridSize.x || cell.y >= GridSize.y)
                continue;

            // distribution_bias controls how likely each horizontal row is
            // to contain a sparkle. Keep every sparkle inside its owning cell
            // so the neighboring-cell lookup remains valid and nothing
            // disappears simply because it was moved too far from its source
            // cell. Positive values favor the top (y = 0); negative values
            // favor the bottom (y = 1).
            float rowY = (cell.y + 0.5f) / max(1.0f, GridSize.y);
            float biasAmount = abs(DistributionBias);
            float rowWeight;
            if (DistributionBias > 0.0f)
                rowWeight = pow(max(0.0f, 1.0f - rowY), biasAmount);
            else if (DistributionBias < 0.0f)
                rowWeight = pow(max(0.0f, rowY), biasAmount);
            else
                rowWeight = 1.0f;

            // Normalize the continuous distribution so bias changes shape
            // the population instead of simply multiplying its overall count.
            // The average of x^b over [0,1] is 1/(b+1).
            float normalizedWeight = rowWeight * (biasAmount + 1.0f);
            float densityChance = min(1.0f, Density * normalizedWeight);
            if (HashCell(cell, 101.0f) > densityChance)
                continue;

            float2 randomPosition = lerp(0.12f, 0.88f, HashCell2(cell, 203.0f));
            float2 center = float2(
                (cell.x + randomPosition.x) / GridSize.x,
                (cell.y + randomPosition.y) / GridSize.y);

            float sizeRoll = HashCell(cell, 307.0f);
            float size = lerp(MinSize, MaxSize, sizeRoll);

            float cycleLength = lerp(CycleMin, CycleMax, HashCell(cell, 409.0f));
            cycleLength = max(0.05f, cycleLength);
            float cycleIndex = floor(Time / cycleLength);
            float cycleT = frac(Time / cycleLength);

            float cycleSeed = cell.x + cell.y * GridSize.x + cycleIndex * 37.17f;
            float flashCenter = Hash(cycleSeed + RandomSeed * 3.7f + 501.0f);
            float flashWidth = lerp(
                FlashWidthMin,
                FlashWidthMax,
                Hash(cycleSeed + 607.0f));
            flashWidth = max(0.001f, flashWidth);

            float activeRoll = Hash(cycleSeed + 719.0f);
            if (activeRoll > BurstChance)
                continue;

            // flashDelta is the normalized time from the peak, wrapped so the
            // rise can cross the end of a cycle when the peak is near 0.
            float flashDelta = cycleT - flashCenter;
            flashDelta -= floor(flashDelta);

            // One-sided animation:
            //   start low -> eased rise -> peak -> fade while falling -> gone.
            // The rise deliberately occupies a visible fraction of the cycle
            // instead of being tied only to the very short flash width.
            const float riseWindow = 0.22f;
            float opacity = 0.0f;
            float jumpPhase = 0.0f;

            if (flashDelta <= flashWidth)
            {
                // Peak -> fade out while falling back down.
                float t = saturate(flashDelta / flashWidth);
                float easedFade = t * t * (3.0f - 2.0f * t);
                opacity = 1.0f - easedFade;
                jumpPhase = 1.0f + t;
            }
            else
            {
                float timeBeforePeak = 1.0f - flashDelta;
                if (timeBeforePeak > riseWindow)
                    continue;

                // Start low and ease into the peak. Keep the light visible
                // during the launch so the upward motion is obvious.
                float t = saturate(1.0f - timeBeforePeak / riseWindow);
                float easedRise = 1.0f - pow(1.0f - t, 3.0f);
                opacity = easedRise;
                jumpPhase = t;
            }

            // Keep the launch visibly present. The old curve applied the
            // sharpness exponent to the entire rise, making the sparkle almost
            // invisible until it was already near the peak. During the fall we
            // keep the sharper fade, while the rise gets a small visible base.
            float flash;
            if (jumpPhase < 1.0f)
            {
                float riseVisible = 0.18f + 0.82f * saturate(opacity);
                flash = riseVisible;
            }
            else
            {
                flash = pow(saturate(opacity), max(1.0f, Sharpness));
            }
            flash *= Brightness;
            if (flash <= 0.0001f)
                continue;

            float jumpAmount = lerp(MinJump, MaxJump, HashCell(cell, 823.0f));
            const float startOffset = 0.35f;
            const float peakOffset = -0.65f;
            const float endOffset = 0.10f;

            float jumpOffset;
            if (jumpPhase < 1.0f)
            {
                // Smooth/eased launch from the low position to the peak.
                float t = saturate(jumpPhase);
                float eased = 1.0f - pow(1.0f - t, 3.0f);
                jumpOffset = lerp(startOffset, peakOffset, eased);
            }
            else
            {
                // Smooth fall from the peak to slightly below it while fading.
                float t = saturate(jumpPhase - 1.0f);
                float eased = t * t * (3.0f - 2.0f * t);
                jumpOffset = lerp(peakOffset, endOffset, eased);
            }

            center.y += jumpOffset * jumpAmount;

            // Convert the fragment offset into square-ish local units so the
            // sparkle keeps its intended shape on a wide quad.
            float2 delta = uv - center;
            delta.x *= max(0.0001f, QuadAspect);

            float angle = (HashCell(cell, 811.0f) - 0.5f) * 6.2831853f;
            float s = sin(angle);
            float c = cos(angle);
            float2 rotated = float2(
                delta.x * c - delta.y * s,
                delta.x * s + delta.y * c);

            // Exact sprite bounds. Do not use the grid cell as a bounding box:
            // this lets the complete sparkle texture cross any grid boundary.
            if (abs(rotated.x) > size * max(0.0001f, TextureAspect) ||
                abs(rotated.y) > size)
                continue;

            float2 sparkleUv = float2(
                rotated.x / max(0.0001f, size * 2.0f * TextureAspect),
                rotated.y / max(0.0001f, size * 2.0f)) + 0.5f;

            sparkleUv.y = 1.0f - sparkleUv.y;
            float4 sparkle = tex2D(TextureSampler, sparkleUv);
            result += sparkle * flash;
        }
    }

    result *= input.Color;
    result.a = saturate(result.a);
    return result;
}


float4 BakeSparklesPS(VSOutput input) : COLOR0
{
    float2 uv = saturate(input.TexCoord);
    float2 baseCell = floor(uv * GridSize);
    float4 result = float4(0, 0, 0, 0);

    // This technique is used only when generating the baked 50 FPS / 1 second
    // animation atlas. Every sparkle has a deterministic phase in that loop,
    // so frame 0 and frame 50 represent the same state and the clip loops cleanly.
    // The grid is a placement lattice only; neighboring cells are searched so a
    // sparkle's full texture can cross cell boundaries.
    for (int oy = -2; oy <= 2; oy++)
    {
        for (int ox = -2; ox <= 2; ox++)
        {
            float2 cell = baseCell + float2((float)ox, (float)oy);
            if (cell.x < 0.0f || cell.y < 0.0f ||
                cell.x >= GridSize.x || cell.y >= GridSize.y)
                continue;

            // Bias the row's activation probability rather than moving the
            // sparkle outside its owning cell. This keeps the cell lookup
            // coherent while making the overall population top- or bottom-
            // heavy.
            float rowY = (cell.y + 0.5f) / max(1.0f, GridSize.y);
            float biasAmount = abs(DistributionBias);
            float rowWeight;
            if (DistributionBias > 0.0f)
                rowWeight = pow(max(0.0f, 1.0f - rowY), biasAmount);
            else if (DistributionBias < 0.0f)
                rowWeight = pow(max(0.0f, rowY), biasAmount);
            else
                rowWeight = 1.0f;

            float normalizedWeight = rowWeight * (biasAmount + 1.0f);
            float densityChance = min(1.0f, Density * normalizedWeight);
            if (HashCell(cell, 101.0f) > densityChance)
                continue;

            float sparkleTextureIndex = floor(HashCell(cell, 913.0f) * max(1.0f, TextureCount));
            if (sparkleTextureIndex != TextureIndex)
                continue;

            float2 randomPosition = lerp(0.12f, 0.88f, HashCell2(cell, 203.0f));
            float2 center = float2(
                (cell.x + randomPosition.x) / GridSize.x,
                (cell.y + randomPosition.y) / GridSize.y);

            float size = lerp(MinSize, MaxSize, HashCell(cell, 307.0f));
            float jumpAmount = lerp(MinJump, MaxJump, HashCell(cell, 823.0f));

            if (HashCell(cell, 719.0f) > BurstChance)
                continue;

            // One flash/jump is assigned to each sparkle's 1-second loop phase.
            // A random phase keeps the crowd asynchronous while the complete
            // animation remains perfectly loopable.
            float phase = frac(Time + HashCell(cell, 501.0f));
            float flashWidth = lerp(FlashWidthMin, FlashWidthMax, HashCell(cell, 607.0f));
            flashWidth = max(0.01f, flashWidth);

            const float riseWindow = 0.20f;
            const float peakPhase = 0.38f;
            float phaseToPeak = phase - peakPhase;

            // Wrap into the nearest signed distance from the peak.
            phaseToPeak -= floor(phaseToPeak + 0.5f);

            float jumpOffset = 0.0f;
            float opacity = 0.0f;

            if (phaseToPeak < 0.0f && -phaseToPeak <= riseWindow)
            {
                // Low -> peak. Ease-out cubic makes the jump start quickly and
                // settle naturally into the peak.
                float t = saturate(1.0f + phaseToPeak / riseWindow);
                float eased = 1.0f - pow(1.0f - t, 3.0f);
                jumpOffset = lerp(0.35f, -0.95f, eased) * jumpAmount;
                opacity = 0.18f + 0.82f * eased;
            }
            else if (phaseToPeak >= 0.0f && phaseToPeak <= flashWidth)
            {
                // Peak -> fall. The sparkle is fully bright at the start of the
                // fade, then eases downward as it disappears.
                float t = saturate(phaseToPeak / flashWidth);
                float eased = t * t * (3.0f - 2.0f * t);
                jumpOffset = lerp(-0.95f, 0.15f, eased) * jumpAmount;
                opacity = 1.0f - eased;
                opacity = pow(saturate(opacity), max(1.0f, Sharpness));
            }
            else
            {
                continue;
            }

            float flash = max(0.0f, opacity) * Brightness;
            if (flash <= 0.0001f)
                continue;

            center.y += jumpOffset;

            float2 delta = uv - center;
            delta.x *= max(0.0001f, QuadAspect);

            float angle = (HashCell(cell, 811.0f) - 0.5f) * 6.2831853f;
            float s = sin(angle);
            float c = cos(angle);
            float2 rotated = float2(
                delta.x * c - delta.y * s,
                delta.x * s + delta.y * c);

            // Do not use the grid cell as a clipping boundary. This is the
            // sparkle's own rectangle, so its texture remains intact even when
            // it overlaps neighboring grid cells.
            if (abs(rotated.x) > size * max(0.0001f, TextureAspect) ||
                abs(rotated.y) > size)
                continue;

            float2 sparkleUv = float2(
                rotated.x / max(0.0001f, size * 2.0f * TextureAspect),
                rotated.y / max(0.0001f, size * 2.0f)) + 0.5f;
            sparkleUv.y = 1.0f - sparkleUv.y;

            float4 sparkle = tex2D(TextureSampler, sparkleUv);
            result += sparkle * flash;
        }
    }

    result *= input.Color;
    result.a = saturate(result.a);
    return result;
}

technique Sparkles
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}

technique BakedSparkles
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL BakeSparklesPS();
    }
}
