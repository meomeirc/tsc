local track = screen.width / 25
local bank = track * 3
local judgeWidth = track
local is_wide = screen.aspect >= (16 / 9)
local rotation = 0

local hitAnimationWidth = track * 4
local hitAnimationDuration = 0.3
return {
    -- Decorative outer track and its lane line opacity (0..1).
    outerTrackOpacity = 0.5,
    editor = {
        inactiveBankOpacity = 0.5,
    },
    lane = {
        position = {
            x = screen.width / 2,
            y = screen.height - 220,
        },
        gap = { start = 40, judge = 40 },
        rotation = {
            degrees = rotation,
            depthDegrees = 0,
        },
        width = {
            bank = bank,
            track = track,
        },
        depth = {
            farCameraDistance = 0,
            length = 100,
            endLength = 400,
        },
        texture = { width = track },
        line = {
            width = {
                near = track,
                far = track,
            },
        },
        highlight = {
            riseDuration = 1.0,
            alpha = 1,
            blend = add,
        },
    },

    judge = {
        width = judgeWidth,
        alpha = 1,
        blend = normal,
        pulse = { width = judgeWidth * 1.12 },
    },

    note = {
        width = track*1.2,
        standAngle = 0,
    },

    perspective = {
        speedCompensation = 0,
        sizeCompensation = 0,
    },

    hold = {
        indicator = { width = track*1.2 },
        light = {
            width = track*1.2*3,
            pulse = {
                width = track * 1.2 * 3.1,
                opacity = 0.35,
            },
        },
        body = { width = track / 6 },
        tail = { width = track / 5 },
        dim = { width = track * 2 },
    },

    vibrato = {
        indicator = { width = track*1.2 },
        body = { width = track  },
        endTexture = { width = track  },
    },

    smallFlick = {
        note = { width = track*1.2 },
        indicator = { width = track*1.2 },
        attach = {
            width = track / 2.5,
            gap = 0,
        },
    },

    superFlick = {
        texture = { width = track * 0.8 },
        scroll = { speed = 2.25, gap = 0.2 },
        highlight = { width = track*3, speed = 1, opacity = 1 },
        transition = {
            durationSeconds = 0.5,
            angleDegrees = 180,
            overshoot = 1.35,
        },
        orbit = {
            pivotVerticalOffset = 0,
            radius = 0,
        },
    },

    renderDistance = {
        ahead = 2900,
        behind = 400,
    },

    transitionFlash = {
        delaySeconds = 0.025,
        fadeInSeconds = 0.045,
        fadeOutSeconds = 0.3,
        alpha = 1,
        blend = add,
    },

    hitAnimation = {
        regular = {
            enabled = true, frameCount = 17, totalDurationSeconds = hitAnimationDuration,
            width = hitAnimationWidth, rotateWithLane = false,
        },
        multi = {
            enabled = true, frameCount = 17, totalDurationSeconds = hitAnimationDuration,
            width = hitAnimationWidth, rotateWithLane = false,
        },
        long = {
            enabled = true, frameCount = 17, totalDurationSeconds = hitAnimationDuration,
            width = hitAnimationWidth, rotateWithLane = false,
            holdLoopStartFrame = 4, holdLoopEndFrame = 8, holdUsesLoop = true,
        },
        superFlick = {
            outer = {
                enabled = true, frameCount = 17, totalDurationSeconds = hitAnimationDuration,
                width = hitAnimationWidth, rotateWithLane = false,
            },
            inner = {
                enabled = true, frameCount = 17, totalDurationSeconds = hitAnimationDuration,
                width = hitAnimationWidth, rotateWithLane = false,
            },
        },
    },
}
