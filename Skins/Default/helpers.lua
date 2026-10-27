-- Example module for import().

local helpers = {}

function helpers.resolve_pos(pos)
    return {
        x = screen.width * pos.x[1] + pos.x[2],
        y = screen.height * pos.y[1] + pos.y[2]
    }
end

function helpers.clamp(value, minimum, maximum)
    if value < minimum then
        return minimum
    elseif value > maximum then
        return maximum
    end
    return value
end

function helpers.lerp(a, b, t)
    return a + (b - a) * t
end

return helpers
