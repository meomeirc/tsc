-- Reusable page stack for skin-owned sub-screens.
--
-- The host still owns the actual TSC screen (Song Select, Settings, etc.). This
-- manager lets that screen expose additional Lua pages without creating C# game
-- states. A screen definition may provide render(context, params, manager),
-- update(dt, params, manager), input(action, params, manager), on_enter(params,
-- manager), on_leave(params, manager), and on_resume(params, manager).

local Manager = {}

local function require_name(name, operation)
    if type(name) ~= "string" or name == "" then
        error((operation or "screen operation") .. " expects a non-empty screen name", 3)
    end
    return name
end

local function invoke(definition, callback_name, ...)
    local callback = definition and definition[callback_name]
    if type(callback) == "function" then
        return callback(...)
    end
    return nil
end

function Manager.new(options)
    options = options or {}
    local maximum_depth = tonumber(options.max_depth or 32) or 32
    maximum_depth = math.max(1, math.min(128, math.floor(maximum_depth)))

    -- TSC uses MoonSharp's Preset_HardSandbox, which intentionally excludes
    -- the Metatables module (including setmetatable). Copy the methods onto a
    -- plain instance table so this manager works without expanding the sandbox.
    local instance = {
        _screens = {},
        _stack = {},
        _maximum_depth = maximum_depth,
    }
    for name, method in pairs(Manager) do
        if name ~= "new" and type(method) == "function" then
            instance[name] = method
        end
    end
    return instance
end

function Manager:register(name, definition)
    name = require_name(name, "screen_manager:register(name, definition)")
    if type(definition) ~= "table" then
        error("screen_manager:register expects a screen definition table", 2)
    end
    if self._screens[name] ~= nil then
        error("screen '" .. name .. "' is already registered", 2)
    end

    self._screens[name] = definition
    return self
end

-- Adds or replaces selected callbacks on an existing registration. This is
-- useful for skin extensions that want to add a pre-game route to the host's
-- built-in Song Select page without replacing its renderer.
function Manager:extend(name, additions)
    name = require_name(name, "screen_manager:extend(name, additions)")
    if type(additions) ~= "table" then
        error("screen_manager:extend expects an additions table", 2)
    end

    local definition = self._screens[name]
    if definition == nil then
        error("cannot extend unknown screen '" .. name .. "'", 2)
    end

    for key, value in pairs(additions) do
        definition[key] = value
    end
    return self
end

function Manager:_definition(name, operation)
    name = require_name(name, operation)
    local definition = self._screens[name]
    if definition == nil then
        error("unknown screen '" .. name .. "'", 3)
    end
    return name, definition
end

function Manager:_push(name, params)
    local _, definition = self:_definition(name, "screen_manager:open(name, params)")
    if #self._stack >= self._maximum_depth then
        error("screen stack limit reached (" .. tostring(self._maximum_depth) .. ")", 2)
    end

    local previous = self._stack[#self._stack]
    if previous then
        invoke(previous.definition, "on_suspend", previous.params, self)
    end

    local entry = { name = name, definition = definition, params = params }
    self._stack[#self._stack + 1] = entry
    invoke(definition, "on_enter", params, self)
    return self
end

function Manager:set_root(name, params)
    name = require_name(name, "screen_manager:set_root(name, params)")
    for index = #self._stack, 1, -1 do
        local entry = self._stack[index]
        invoke(entry.definition, "on_leave", entry.params, self)
        self._stack[index] = nil
    end
    return self:_push(name, params)
end

function Manager:open(name, params)
    return self:_push(name, params)
end

function Manager:replace(name, params)
    name = require_name(name, "screen_manager:replace(name, params)")
    if #self._stack > 0 then
        local previous = self._stack[#self._stack]
        invoke(previous.definition, "on_leave", previous.params, self)
        self._stack[#self._stack] = nil
    end
    return self:_push(name, params)
end

function Manager:back()
    if #self._stack <= 1 then
        return false
    end

    local current = self._stack[#self._stack]
    invoke(current.definition, "on_leave", current.params, self)
    self._stack[#self._stack] = nil

    local resumed = self._stack[#self._stack]
    if resumed then
        invoke(resumed.definition, "on_resume", resumed.params, self)
    end
    return true
end

function Manager:reset()
    local root = self._stack[1]
    if root == nil then
        return false
    end
    return self:set_root(root.name, root.params)
end

function Manager:current()
    local entry = self._stack[#self._stack]
    if entry == nil then
        return nil, nil, nil
    end
    return entry.name, entry.params, entry.definition
end

function Manager:current_name()
    local entry = self._stack[#self._stack]
    return entry and entry.name or nil
end

function Manager:current_params()
    local entry = self._stack[#self._stack]
    return entry and entry.params or nil
end

function Manager:depth()
    return #self._stack
end

function Manager:contains(name)
    name = require_name(name, "screen_manager:contains(name)")
    for index = #self._stack, 1, -1 do
        if self._stack[index].name == name then
            return true
        end
    end
    return false
end

function Manager:is_registered(name)
    name = require_name(name, "screen_manager:is_registered(name)")
    return self._screens[name] ~= nil
end

function Manager:update(dt, context)
    local entry = self._stack[#self._stack]
    if entry ~= nil then
        invoke(entry.definition, "update", math.max(0, tonumber(dt or 0) or 0), entry.params, self, context)
    end
end

function Manager:render(context)
    local entry = self._stack[#self._stack]
    if entry == nil then
        return false
    end

    local callback = entry.definition.render
    if type(callback) ~= "function" then
        return false
    end

    callback(context, entry.params, self)
    return true
end


-- Routes a ui.button/ui.slider action to the active page. Returning true means
-- the custom page handled it and the C# host should not run its built-in action.
function Manager:action(action, value, context)
    if type(action) ~= "string" or action == "" then
        return false
    end

    local entry = self._stack[#self._stack]
    if entry ~= nil then
        local consumed = invoke(entry.definition, "action", action, value, entry.params, self, context)
        if consumed == true then
            return true
        end
    end

    return false
end

function Manager:input(action, context)
    if type(action) ~= "string" or action == "" then
        return false
    end

    local entry = self._stack[#self._stack]
    if entry ~= nil then
        local consumed = invoke(entry.definition, "input", action, entry.params, self, context)
        if consumed == true then
            return true
        end
    end

    -- Back always closes a pushed page unless that page consumed it itself.
    -- At the root, false is returned so TSC can perform its normal back action.
    if action == "back" and self:back() then
        return true
    end

    return false
end

return {
    new = function(options)
        return Manager.new(options)
    end,
}
