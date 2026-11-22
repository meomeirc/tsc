-- Main menu presentation and mouse hitboxes are fully Lua-customizable.

local UI = import("uiStyle")
local ScreenManager = import("ui/screenManager")
local screen_manager = ScreenManager.new()
-- The logo is supplied by the selected skin; do not load a loose project-root logo.
local LOGO = assets.load("logo.png", true)

function init()
end

function update(dt)
    screen_manager:update(dt)
end

local function render_main_menu()
    local width = screen.width or 1920
    local height = screen.height or 1080
    local style = UI.main_menu or {}
    local page_padding = tonumber(style.page_padding or 48) or 48
    local button_height = tonumber(style.button_height or UI.spacing.button_height) or UI.spacing.button_height
    local button_gap = tonumber(style.button_gap or UI.spacing.button_gap) or UI.spacing.button_gap
    local selected = math.max(0, math.floor(tonumber(menu.selected or 0) or 0))
    local buttons = {
        { id = "play", label = "PLAY", action = "play" },
    }
    if menu.chart_editor_enabled ~= false then
        table.insert(buttons, { id = "chart_editor", label = "CHART EDITOR", action = "chart_editor" })
    end
    table.insert(buttons, { id = "settings", label = "SETTINGS", action = "settings" })
    table.insert(buttons, { id = "info", label = "INFO", action = "info" })
    table.insert(buttons, { id = "exit", label = "EXIT", action = "exit" })
    local buttons_count = #buttons
    local button_width = math.min(
        tonumber(style.button_width or UI.spacing.button_width) or UI.spacing.button_width,
        math.max(1, (width - page_padding * 2 - button_gap * (buttons_count - 1)) / buttons_count))
    local row_width = button_width * buttons_count + button_gap * (buttons_count - 1)
    local row_x = (width - row_width) * 0.5
    local row_y = height * (tonumber(style.button_center_y or 0.78) or 0.78) - button_height * 0.5

    UI.layout_box({ id = "main_viewport", width = width, height = height })

-- Keep the logo's center exactly at the screen's vertical center.
    if LOGO ~= nil then
        local logo_width = math.min(
            tonumber(style.logo_width or 520) or 520,
            math.max(1, width - page_padding * 2))
        local logo_height = logo_width * (LOGO.height / math.max(1, LOGO.width))
        draw.sprite(LOGO, {
            parent = "main_viewport",
            x = width * 0.5,
            y = height * 0.5,
            width = logo_width,
            height = logo_height,
            anchor = "center",
            layer = 1,
        })
    else
        draw.text("TSC", {
            parent = "main_viewport",
            x = width * 0.5,
            y = height * 0.5,
            size = UI.sizes.title,
            color = UI.colors.text,
            font = UI.fonts.bold,
            anchor = "center",
            layer = 1,
        })
    end

    for index, button in ipairs(buttons) do
        UI.draw_button({
            id = button.id,
            parent = "main_viewport",
            x = row_x + (index - 1) * (button_width + button_gap),
            y = row_y,
            width = button_width,
            height = button_height,
            label = button.label,
            text_size = UI.sizes.button,
            font = UI.fonts.bold,
            action = button.action,
            selected = selected == index - 1,
        })
    end

    local version = tostring(menu.version or "0.0.1")
    draw.text("v" .. version, {
        parent = "main_viewport",
        x = page_padding,
        y = height - 18,
        size = UI.sizes.small,
        color = UI.colors.muted,
        font = UI.fonts.regular,
        anchor = "left",
        layer = 4,
    })

    local update_status = tostring(menu.update_status or "")
    if update_status ~= "" then
        local version_width = (#version + 1) * UI.sizes.small * 0.58
        draw.text(update_status, {
            parent = "main_viewport",
            x = page_padding + version_width + 18,
            y = height - 18,
            size = UI.sizes.small,
            color = update_status == "Update available" and UI.colors.accent or UI.colors.muted,
            font = UI.fonts.regular,
            anchor = "left",
            layer = 4,
        })
    end

    local error = menu.error
    if error ~= nil and error ~= "" then
        draw.text(error, {
            parent = "main_viewport",
            x = width * 0.5,
            y = height - 28,
            size = UI.sizes.small,
            color = UI.colors.error,
            font = UI.fonts.regular,
            anchor = "center",
            layer = 4,
        })
    end
end

-- The C# host remains the real Main Menu screen. The page stack lets skins
-- insert their own sub-pages without replacing TSC startup/navigation logic.
screen_manager:register("main_menu", {
    render = render_main_menu,
})
screen_manager:set_root("main_menu")

-- Optional skin extension: scripts/screens/mainMenu.lua. Missing files are normal.
local extension_ok, extension = pcall(import, "screens/mainMenu")
local extension_registered = false
if extension_ok then
    if type(extension) == "function" then
        extension(screen_manager)
        extension_registered = true
    elseif type(extension) == "table" and type(extension.register) == "function" then
        extension.register(screen_manager)
        extension_registered = true
    else
        print("[screen_manager] screens/mainMenu.lua must return a function or a table with register(manager)")
    end
    if extension_registered then
        print("[screen_manager] loaded screens/mainMenu.lua")
    end
else
    local extension_error = tostring(extension)
    if not string.find(extension_error, "file not found", 1, true) then
        print("[screen_manager] could not load screens/mainMenu.lua: " .. extension_error)
    end
end

function render()
    screen_manager:render()
end

function on_main_menu_input(action)
    return screen_manager:input(action)
end

function on_main_menu_action(action, value)
    return screen_manager:action(action, value)
end
