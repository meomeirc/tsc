-- Playfield/camera configuration only.

-- Use the 16:9 virtual width as the maximum geometry width.
local geometryWidth = math.min(screen.width, screen.height * (16 / 9))

local lane = {
    width = {},
    gap = {},
    position = {},
    rotation = {},
    depth = {},
    line = {
        width = {},
    },
    texture = {
        widthScale = 1,
    },
}

-- 4:3
if screen.aspect < 1.3333 then
    lane.depth.behindCameraDistance = geometryWidth / 7
    lane.width.bank = geometryWidth * 0.36
    lane.gap.start = -25
    lane.position.y = screen.height * 0.87
-- 16:9
else
    lane.depth.behindCameraDistance = geometryWidth / 11
    lane.width.bank = geometryWidth * 0.32
    lane.gap.start = 0
    lane.position.y = screen.height * 0.87
end

lane.width.track = lane.width.bank / 3
lane.gap.judge = geometryWidth * 0.28
lane.position.x = screen.width * 0.5
lane.rotation.degrees = 29
lane.line.width.near = lane.width.track*1
lane.line.width.far = lane.width.track*0.5

local judgeWidth = lane.width.track * 1.2
local noteWidth = lane.width.track * 1.35
local hitAnimationWidth = lane.width.track * 4
local hitAnimationDuration = 0.3

return {
    -- Decorative outer track and its lane line opacity (0..1).
    outerTrackOpacity = 0.5,
    lane = {
        position = {
            x = lane.position.x,
            y = lane.position.y,
        },

        gap = {
            start = lane.gap.start,
            judge = lane.gap.judge,
        },

        width = {
            bank = lane.width.bank,
            track = lane.width.track,
        },

        rotation = {
            degrees = lane.rotation.degrees,
        },

        depth = {
            farCameraDistance = 9000,
            behindCameraDistance = lane.depth.behindCameraDistance,
            length = 100,
            endLength = 100
        },

        line = {
            width = {
                near = lane.line.width.near,
                far = lane.line.width.far,
            },
        },

        texture = {
            widthScale = lane.texture.widthScale,
        },

        highlight = {
            riseDuration = 0.2,
            alpha = 1,
            blend = "add",
        },
    },

    judge = {
        width = judgeWidth,

        pulse = {
            width = judgeWidth * 1.1,
        },
    },

    note = {
        width = noteWidth,
        standAngle = 40,
    },

    perspective = {
        speedCompensation = 10,
        sizeCompensation = 15,
    },

    hold = {
        indicator = {
            width = noteWidth,
        },

        light = {
            width = noteWidth * 3,

            pulse = {
                width = noteWidth * 3 * 1.1,
                opacity = 0.35,
            },
        },

        body = {
            width = noteWidth / 6,
        },

        tail = {
            width = noteWidth / 4,
        },

        dim = {
            width = noteWidth * 0.8,
        },
    },

    vibrato = {
        indicator = {
            width = noteWidth * 1.5,
        },

        body = {
            width = noteWidth,
        },

        endTexture = {
            width = noteWidth,
        },
    },

    smallFlick = {
        note = {
            width = noteWidth,
        },

        indicator = {
            width = noteWidth,
        },

        attach = {
            width = noteWidth / 2,
            gap = 0,
        },
    },

    superFlick = {
        texture = {
            width = noteWidth * 0.7,
        },

        scroll = {
            speed = 2.25,
            gap = 0.2,
        },

        highlight = {
            width = noteWidth * 3,
            speed = 2,
            opacity = 1,
        },

        transition = {
            durationSeconds = 0.4,
            angleDegrees = 180,
            overshoot = 1,
        },

        orbit = {
            pivotVerticalOffset = 0,
            radiusScale = 0.5,
        },
    },

    renderDistance = {
        ahead = 2400,
        behind = 100,
    },

    transitionFlash = {
        delaySeconds = 0.1,
        fadeInSeconds = 0.045,
        fadeOutSeconds = 0.3,
        opacity = 1,
    },

    hitAnimation = {
        regular = {
            enabled = true,
            frameCount = 17,
            totalDurationSeconds = hitAnimationDuration,
            width = hitAnimationWidth,
            rotateWithLane = false,
        },

        multi = {
            enabled = true,
            frameCount = 17,
            totalDurationSeconds = hitAnimationDuration,
            width = hitAnimationWidth,
            rotateWithLane = false,
        },

        long = {
            enabled = true,
            frameCount = 17,
            totalDurationSeconds = hitAnimationDuration,
            width = hitAnimationWidth,
            rotateWithLane = false,

            holdLoop = {
                startFrame = 4,
                endFrame = 8,
                usesLoop = true,
            },
        },

        superFlick = {
            outer = {
                enabled = true,
                frameCount = 17,
                totalDurationSeconds = hitAnimationDuration,
                width = hitAnimationWidth,
                rotateWithLane = true,
            },

            inner = {
                enabled = true,
                frameCount = 17,
                totalDurationSeconds = hitAnimationDuration,
                width = hitAnimationWidth,
                rotateWithLane = true,
            },
        },
    },
}
