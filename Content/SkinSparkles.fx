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


// -----------------------------------------------------------------------------
// Procedural crowd bake (8 static layers + one reusable purple flash sprite)
// -----------------------------------------------------------------------------
// The crowd is divided deterministically between eight full-resolution layers.
// Each layer is baked once; the runtime only applies a small whole-layer jump
// and opacity loop. Purple flashes use one separate reusable texture and are
// instanced at deterministic random positions by the C# renderer.
float2 CrowdResolution;
float LayerIndex;
float LayerCount;
float CrowdSeed;

float Hash21(float2 p)
{
    p = frac(p * float2(123.34f, 456.21f));
    p += dot(p, p + 45.32f);
    return frac(p.x * p.y);
}

float3 HexColorCrowd(float3 rgb)
{
    return rgb / 255.0f;
}

float4 BakeCrowdLayerPS(VSOutput input) : COLOR0
{
    const float CELL_SIZE = 40.0f;
    const float DOT_DENSITY = 5.0f;
    const float DOT_DISTRIBUTION_BIAS = 10.0f;
    const float DOT_MIN_DENSITY = 0.08f;
    const float DOT_POSITION_JITTER = 1.15f;
    const float DOT_SIZE = 9.4f;
    const float DOT_SIZE_VARIATION = 1.2f;
    const float GLOW_SIZE = 0.5f;
    const float GLOW_SIZE_VARIATION = 0.08f;
    const float GLOW_STRENGTH = 2.0f;
    const float GLOW_FALLOFF = 2.0f;
    const float CORE_BRIGHTNESS = 1.0f;
    const float CORE_HIGHLIGHT = 0.8f;
    const float REFERENCE_HEIGHT = 1080.0f;

    float2 uv = saturate(input.TexCoord);
    // The source shader uses bottom-left fragment coordinates. Keep its
    // positive distribution bias focused toward the visual top of the image.
    float2 fragCoord = float2(uv.x * CrowdResolution.x,
                              (1.0f - uv.y) * CrowdResolution.y);
    float scale = CrowdResolution.y / REFERENCE_HEIGHT;
    float cellSize = CELL_SIZE * scale;
    float2 currentCell = floor(fragCoord / cellSize);
    float3 accumulatedRgb = float3(0.0f, 0.0f, 0.0f);
    float accumulatedAlpha = 0.0f;

    // With movement applied to each layer at runtime, only the modest random
    // spawn jitter and dot radius can cross a cell boundary during the bake.
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 cell = currentCell + float2((float)x, (float)y);
            float r1 = Hash21(cell + float2(12.3f, 45.6f) + CrowdSeed);
            float r2 = Hash21(cell + float2(78.9f, 12.3f) + CrowdSeed);
            float r3 = Hash21(cell + float2(34.5f, 67.8f) + CrowdSeed);
            float r5 = Hash21(cell + float2(56.7f, 89.1f) + CrowdSeed);
            float r6 = Hash21(cell + float2(21.4f, 73.2f) + CrowdSeed);
            float layerRoll = Hash21(cell + float2(31.4f, 92.3f) + CrowdSeed);

            if (floor(layerRoll * max(1.0f, LayerCount)) != LayerIndex)
                continue;

            float2 offset = float2(0.5f, 0.5f) +
                (float2(r1, r2) - 0.5f) * DOT_POSITION_JITTER;
            float2 center = (cell + offset) * cellSize;

            float anchorY = saturate(center.y / max(1.0f, CrowdResolution.y));
            float shaped = pow(saturate(anchorY), DOT_DISTRIBUTION_BIAS);
            float distribution = lerp(DOT_MIN_DENSITY, 1.0f, shaped);
            float spawnChance = saturate(DOT_DENSITY * distribution);
            if (r6 > spawnChance)
                continue;

            float dist = length(fragCoord - center);
            float dotRadius = (DOT_SIZE + r1 * DOT_SIZE_VARIATION) * scale;
            float glowRadius = cellSize * (GLOW_SIZE + r3 * GLOW_SIZE_VARIATION);
            float glow = pow(max(1.0f - dist / max(0.001f, glowRadius), 0.0f), GLOW_FALLOFF)
                * GLOW_STRENGTH;
            float core = 1.0f - smoothstep(dotRadius * 0.35f, dotRadius, dist);
            float alpha = saturate(max(glow, core * CORE_BRIGHTNESS));

            float3 dotColor = lerp(
                HexColorCrowd(float3(51.0f, 166.0f, 255.0f)),
                HexColorCrowd(float3(122.0f, 184.0f, 255.0f)),
                r5);
            float3 sourceColor = lerp(
                dotColor * 0.8f,
                float3(1.0f, 1.0f, 1.0f),
                saturate(core * CORE_HIGHLIGHT));

            // Match the source shader's normal alpha compositing. Store the
            // completed colour premultiplied so the baked layer can be drawn
            // with the standard AlphaBlend state at runtime.
            accumulatedRgb = sourceColor * alpha + accumulatedRgb * (1.0f - alpha);
            accumulatedAlpha = alpha + accumulatedAlpha * (1.0f - alpha);
        }
    }

    return float4(accumulatedRgb, accumulatedAlpha);
}

float4 BakePurpleFlashPS(VSOutput input) : COLOR0
{
    const float STAR_SIZE = 20.0f;
    const float STAR_RAY_LENGTH = 2.5f;
    const float STAR_RAY_THICKNESS = 0.5f;
    const float STAR_RAY_STRENGTH = 2.0f;
    const float STAR_GLOW_RADIUS = 38.0f;
    const float STAR_GLOW_STRENGTH = 0.6f;

    float scale = CrowdResolution.y / 1080.0f;
    // Bake into a 512px canvas so the long exponential ray tails reach
    // their natural fade instead of being cut at the edge of a 128px box.
    float2 delta = (saturate(input.TexCoord) - 0.5f) * 512.0f;
    float starSize = STAR_SIZE * scale;
    float dx = abs(delta.x);
    float dy = abs(delta.y);

    float horizontalRay =
        exp(-dy / max(0.001f, starSize * STAR_RAY_THICKNESS)) *
        exp(-dx / max(0.001f, starSize * STAR_RAY_LENGTH));
    float verticalRay =
        exp(-dx / max(0.001f, starSize * STAR_RAY_THICKNESS)) *
        exp(-dy / max(0.001f, starSize * STAR_RAY_LENGTH));
    float rays = max(horizontalRay, verticalRay) * STAR_RAY_STRENGTH;
    float core = exp(-dot(delta, delta) / max(0.001f, starSize * starSize * 0.12f));
    float glowRadius = STAR_GLOW_RADIUS * scale;
    float glow = exp(-dot(delta, delta) / max(0.001f, glowRadius * glowRadius))
        * STAR_GLOW_STRENGTH;
    float shape = max(glow, max(rays, core));
    float alpha = saturate(shape);
    float3 starColor = lerp(
        HexColorCrowd(float3(148.0f, 51.0f, 255.0f)),
        float3(1.0f, 1.0f, 1.0f),
        saturate(core));

    return float4(starColor * alpha, alpha);
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


technique BakedCrowdLayer
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL BakeCrowdLayerPS();
    }
}

technique BakedPurpleFlash
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL BakePurpleFlashPS();
    }
}
