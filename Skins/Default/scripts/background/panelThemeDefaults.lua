-- Built-in presentation defaults for the animated background panels.
-- Custom skins can override only the fields they need in panelTheme.lua.

return {
    assets = {
        panel_base = "panel-base.png",
        podium = "podium.png",
        characters = { [1] = "character1.png", [2] = "character2.png", [3] = "character3.png", [4] = "character4.png" },
        light = "light-cone.png",
    },
    colors = {
        by_element = { DANCER = "#784649", VOCAL = "#435A73", SESSION = "#48664D" },
    },
    geometry = {
        panel_layer = -100, width = 460, gap = 28,
        depths = { -220, -300, -300, -220 },
        yaws_degrees = { 50, 10, -10, -50 },
        character_layer_offset = 2, light_layer_offset = 3,
    },
    animation = {
        character = {
            windowScale = 0.72,
            flashOpacity = 0.70,
            flashDuration = 0.7,
            neutralBreathing = { maxZoom = 1.05, zoomInDuration = 3.0, zoomOutDuration = 3.0 },

            [101] = { kind = "vertical", from = { 0.50, 0.5 }, to = { 0.50, 0.9 }, zoom = 0.75, duration = 5.0, fadeOutDuration = 2,
                flashFadeDuration = 2.5 },

            [102] = { kind = "vertical", from = { 0.50, 0.9 }, to = { 0.50, 0.5 }, zoom = 0.75, duration = 5.0, fadeOutDuration = 2,
                flashFadeDuration = 2.5 },

            [103] = {
                kind = "cornerLoop",
                secondsPerCorner = 2.0,
                path = { "topLeft", "topRight", "bottomRight", "bottomLeft" },
                scale = 0.60,
                padding = { left = 0.70, right = 0.70, top = 0.0, bottom = 0.0 },
            },

            [104] = {
                kind = "cornerLoop",
                secondsPerCorner = 2.0,
                path = { "topRight", "topLeft", "bottomLeft", "bottomRight" },
                scale = 0.60,
                padding = { left = 0.70, right = 0.70, top = 0.0, bottom = 0.0 },
            },

            -- Horizontal animations use one duration for both travel and fade timing.
            -- fadeOutDuration is the final part of duration, overlapping the movement.
            [105] = { kind = "horizontal", from = { 0.9, 0.5 }, to = { 0.1, 0.5 }, windowScale = 0.72, duration = 4.0, fadeOutDuration = 2 },

            [106] = { kind = "horizontal", from = { 0.1, 0.5 }, to = { 0.9, 0.5 }, windowScale = 0.72, duration = 4.0, fadeOutDuration = 2 },

            [107] = {
                kind = "specialSnap",
                -- Per-animation flash settings override the character-wide defaults above.
                flashOpacity = 0.70,
                flashDuration = 0.5,
                snapPositions = {
                    [1] = { x = 0.4, y = 0.36 }, [2] = { x = 0.6, y = 0.36 },
                    [3] = { x = 0.4, y = 0.64 }, [0] = { x = 0.6, y = 0.64 }
                },
            },

            [108] = {
                kind = "specialZoom", movementDuration = 3.0, holdDuration = 1.0, pivotY = 0.3,
                -- Per-animation flash settings override the character-wide defaults above.
                flashOpacity = 0.70,
                flashDuration = 0.5,
                cycleScales = { [1] = { 0.40, 0.50 }, [2] = { 0.50, 0.60 }, [3] = { 0.60, 0.70 }, [0] = { 0.80, 0.90 } },
            },
        },

        light = {
            topY = -100,
            xOffset = 0.2,
            bottomOverhang = 0.20,
            presentation = {
                introDuration = 1.0,
                outroWidenDuration = 1.0,
                outroFadeDuration = 0.25,
            },

            neutral = {
                opacity = 0.75,
                width = 240,
                swingLeftFrom = -14,
                swingLeftTo = 14,
                swingRightFrom = -14,
                swingRightTo = 14,
                swingDuration = 3.0,
                leftColor = "#7778E8",
                rightColor = "#FFE46A",
            },

            wideWidth = 1000,
            narrowWidth = 240,

            [101] = { duration = 5.0, swingDuration = 5.0, swingLeftFrom = -14, swingLeftTo = 14,
                swingRightFrom = 14, swingRightTo = -14, alphaFrom = 0.75, alphaTo = 0.00, widthFrom = "narrow", widthTo = "narrow" },

            [102] = { duration = 5.0, swingDuration = 5.0, swingLeftFrom = 14, swingLeftTo = 0,
                swingRightFrom = -14, swingRightTo = 0, alphaFrom = 0.75, alphaTo = 0.00, widthFrom = "wide", widthTo = "narrow" },

            [103] = { duration = 8.0, swingDuration = 3.0, swingLeftFrom = -14, swingLeftTo = 14,
                swingRightFrom = 14, swingRightTo = -14, swingLoop = true, alphaFrom = 0.75, alphaTo = 0.025, widthFrom = "wide", widthTo = "narrow" },

            [104] = { duration = 8.0, swingDuration = 3.0, swingLeftFrom = -14, swingLeftTo = 14,
                swingRightFrom = 14, swingRightTo = -14, swingLoop = true, alphaFrom = 0.75, alphaTo = 0.025, widthFrom = "wide", widthTo = "narrow" },

            [105] = { duration = 5.0, swingDuration = 5.0, swingLeftFrom = -14, swingLeftTo = 14,
                swingRightFrom = 14, swingRightTo = -14, alphaFrom = 0.75, alphaTo = 0.25, widthFrom = "wide", widthTo = "narrow" },

            [106] = { duration = 5.0, swingDuration = 5.0, swingLeftFrom = -14, swingLeftTo = 14,
                swingRightFrom = 14, swingRightTo = -14, alphaFrom = 0.75, alphaTo = 0.25, widthFrom = "wide", widthTo = "narrow" },

            [107] = { peakOpacity = 0.75, fadeDuration = 1.0, movementDuration = 1.0,
                swingDuration = 3.0, swingLeftFrom = 0, swingLeftTo = 0, swingRightFrom = 0, swingRightTo = 0 },

            [108] = { peakOpacity = 0.75, fadeDuration = 1.0, movementDuration = 1.0,
                swingDuration = 3.0, swingLeftFrom = 0, swingLeftTo = 0, swingRightFrom = 0, swingRightTo = 0 },
        },

        specialCycle = {
            [1] = { position = "topLeft", color = "#4DA6FF" },
            [2] = { position = "topRight", color = "#FF4D55" },
            [3] = { position = "bottomLeft", color = "#FF9A3C" },
            [0] = { position = "bottomRight", color = "#62D84E" },
        },
    },
}
