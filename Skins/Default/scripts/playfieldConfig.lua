local function get(view)
    local isEditor = view == "editor"

    local geometryWidth = math.min(
        screen.width,
        screen.height * (16 / 9)
    )

    -- Shared note sizing.
    local trackWidth
    local bankWidth

    if isEditor then
        trackWidth = screen.width / 28
        bankWidth = trackWidth * 3
    else
        local bankScale = screen.aspect < 1.3333 and 0.36 or 0.32
        bankWidth = geometryWidth * bankScale
        trackWidth = bankWidth / 3
    end

    local judgeWidth = trackWidth * 1.2
    local noteWidth = trackWidth * 1.35
    local hitAnimationWidth = trackWidth * 4
    local hitAnimationDuration = 0.3

    -- Spacing between Backward / Center / Forward banks on each side.
    -- This is separate from lane.gap.start/judge, which controls the
    -- distance between the left and right sides.
    local bankGapRatio = 0.25
    local bankGapJudge = bankWidth * bankGapRatio
    local farCameraDistance = isEditor and 0 or 9000
    local bankGapStart = bankGapJudge * (350 * 4) /
        (350 * 4 + farCameraDistance)

    -- Shared hit animation configuration.
    local function makeHitAnimation(rotate, long)
        local animation = {
            enabled = true,
            frameCount = 17,
            totalDurationSeconds = hitAnimationDuration,
            width = hitAnimationWidth,
            rotateWithLane = rotate,
        }

        if long then
            if isEditor then
                animation.holdLoopStartFrame = 4
                animation.holdLoopEndFrame = 8
                animation.holdUsesLoop = true
            else
                animation.holdLoop = {
                    startFrame = 4,
                    endFrame = 8,
                    usesLoop = true,
                }
            end
        end

        return animation
    end

    local cameraBehindDistance = geometryWidth /
        (screen.aspect < 1.3333 and 7 or 11)

    local config = {
        animations = {
            laneHighlight = {
                releaseDurationSeconds = 0.20,
                alpha = 1.0,
                blend = "add",
            },

            transitionFlash = {
                delaySeconds = isEditor and 0.025 or 0.10,
                fadeInSeconds = 0.045,
                fadeOutSeconds = 0.30,
                alpha = 1.0,
                blend = "add",
            },

            laneLifecycle = {
                intro = { durationSeconds = 0.45 },
                outro = { durationSeconds = 0.35 },
            },
        },

        outerTrackOpacity = 0.5,

        lane = {
            position = {
                x = screen.width * 0.5,
                y = isEditor and (screen.height - 220)
                    or (screen.height * 0.87),
            },

            gap = {
                start = isEditor and 60
                    or (screen.aspect < 1.3333 and -25 or 0),

                judge = isEditor and 60
                    or (geometryWidth * 0.28),

                bank = {
                    start = bankGapStart,
                    judge = bankGapJudge,
                },
            },

            width = {
                bank = bankWidth,
                track = trackWidth,
            },

            rotation = {
                degrees = isEditor and 0 or 29,
            },

            depth = {
                farCameraDistance = isEditor and 0 or 9000,
                length = 100,
                endLength = isEditor and 300 or 100,
            },

            line = {
                width = {
                    near = trackWidth,
                    far = isEditor and trackWidth or (trackWidth * 0.5),
                },
            },
        },

        judge = {
            width = judgeWidth,
        },

        note = {
            width = noteWidth,
            standAngle = isEditor and 0 or 40,
        },

        perspective = {
            speedCompensation = isEditor and 0 or 10,
            sizeCompensation = isEditor and 0 or 15,
        },

        hold = {
            indicator = {
                width = noteWidth,
            },

            light = {
                width = noteWidth * 3,
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
                durationSeconds = isEditor and 0.5 or 0.4,
                angleDegrees = 180,
                overshoot = isEditor and 1.35 or 1,
            },

            orbit = {
                pivotVerticalOffset = 0,
            },
        },

        renderDistance = {
            ahead = isEditor and 2900 or 2400,
            behind = isEditor and 300 or 100,
        },

        hitAnimation = {
            regular = makeHitAnimation(false, false),
            multi = makeHitAnimation(false, false),
            long = makeHitAnimation(false, true),

            superFlick = {
                outer = makeHitAnimation(not isEditor, false),
                inner = makeHitAnimation(not isEditor, false),
            },
        },
    }

    -- Properties that exist only in the editor.
    if isEditor then
        config.editor = {
            inactiveBankOpacity = 0.5,
        }

        config.judge.alpha = 1
        config.judge.blend = "normal"

        config.lane.rotation.depthDegrees = 0
        config.lane.depth.farCameraDistance = 0
        config.lane.depth.endLength = 300

        config.lane.texture = {
            width = trackWidth,
        }

        config.superFlick.orbit.radius = 0
    else
        -- Gameplay-only properties.
        config.lane.depth.behindCameraDistance = cameraBehindDistance

        config.lane.texture = {
            widthScale = 1,
        }

        config.lane.highlight = {
            riseDuration = 0.2,
            alpha = 1,
            blend = "add",
        }

        config.superFlick.orbit.radiusScale = 0.5
    end

    return config
end

return {
    get = get,
}
