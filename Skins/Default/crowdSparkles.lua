-- Optional convenience wrapper around the native draw.sparkles() effect.

local function render(options)
    draw.sparkles(options or {})
end

return {
    render = render,
}
