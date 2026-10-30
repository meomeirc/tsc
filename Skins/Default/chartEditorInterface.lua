-- The actual chart editor skin. All editor chrome is submitted through the
-- same draw.* and ui.* APIs used by songSelect and gameplayResult. C# owns chart
-- editing and sends this script semantic state/actions; this file owns layout,
-- fonts, colors, hover treatment, and every visible control.

local UI = import("uiStyle")
local C = UI.colors
local F = UI.fonts
local ACTIVE_SCALE = 1

local function fontSize(name)
    return (UI.sizes[name] or UI.sizes.body) * ACTIVE_SCALE
end

local LAYOUT = {
    margin = 16,
    gap = 6,
    panel_gap = 10,
    button_height = 40,
    bottom_height = 66,
    right_dock_width = 390,
    scroll_speed_width = 160,
    timeline_width = 126,
    action_column_width = 200,
    toolbar_rail_width = 152,
    panels_toolbar_width = 200,
    left_toolbar_max_width = 960,
    bottom_action_columns = 7,
    bottom_action_height = 40,
    bottom_action_gap = 6,
    bottom_action_padding = 6,
    left_rail_width = 360,
    panel_padding = 12,
    row_gap = 6,
    text_regular = UI.sizes.body,
    text_medium = UI.sizes.body,
    caption = UI.sizes.small,
    problem_rows = 5,
    preview_width = 860,
    preview_height = 500,
    preview_slider_y = 205,
    preview_input_y = 295,
    preview_side_margin = 62,
}

local function num(value, fallback)
    value = tonumber(value)
    if value == nil then return fallback end
    return value
end

local function clamp(value, low, high)
    return math.max(low, math.min(high, value))
end

local function fittedText(text, width, size)
    text = tostring(text or "")
    local maxChars = math.max(1, math.floor(width / math.max(1, size * 0.56)))
    local characterCount = 0
    for index = 1, #text do
        local byte = string.byte(text, index)
        if byte < 128 or byte >= 192 then characterCount = characterCount + 1 end
    end
    if characterCount <= maxChars then return text end
    if maxChars <= 1 then return "…" end
    local index = 1
    local included = 0
    while index <= #text and included < maxChars - 1 do
        local byte = string.byte(text, index)
        index = index + (byte < 128 and 1 or byte < 224 and 2 or byte < 240 and 3 or 4)
        included = included + 1
    end
    return string.sub(text, 1, index - 1) .. "…"
end

local function fittedMeasuredText(text, width, size, font)
    text = tostring(text or "")
    width = math.max(0, tonumber(width) or 0)
    size = math.max(1, tonumber(size) or 1)
    font = font or F.regular
    local measureOptions = { font = font, size = size }
    if draw.measure_text(text, measureOptions) <= width then return text end

    local ends = {}
    local index = 1
    while index <= #text do
        local byte = string.byte(text, index)
        index = index + (byte < 128 and 1 or byte < 224 and 2 or byte < 240 and 3 or 4)
        ends[#ends + 1] = math.min(#text, index - 1)
    end

    local low, high, best = 0, #ends, ""
    while low <= high do
        local count = math.floor((low + high) * 0.5)
        local prefix = count == 0 and "" or string.sub(text, 1, ends[count])
        local candidate = prefix .. "…"
        if draw.measure_text(candidate, measureOptions) <= width then
            best = candidate
            low = count + 1
        else
            high = count - 1
        end
    end
    return best
end

-- Split on literal separators instead of Lua patterns. MoonSharp can report
-- "pattern too complex" when a long single-line error message is passed to a
-- non-greedy pattern such as (.-)\n.
local function forEachLiteralLine(text, callback)
    local source = tostring(text or "")
    local startIndex = 1
    while true do
        local newline = string.find(source, "\n", startIndex, true)
        if newline == nil then
            callback(string.sub(source, startIndex))
            break
        end
        callback(string.sub(source, startIndex, newline - 1))
        startIndex = newline + 1
    end
end

local function forEachWord(sourceLine, callback)
    local index = 1
    while index <= #sourceLine do
        local byte = string.byte(sourceLine, index)
        if byte <= 32 then
            index = index + 1
        else
            local startIndex = index
            while index <= #sourceLine and string.byte(sourceLine, index) > 32 do
                index = index + 1
            end
            callback(string.sub(sourceLine, startIndex, index - 1))
        end
    end
end

-- Wrap complete descriptions into measured lines instead of shortening them.
-- This mirrors chartEditorMenu.lua so choice buttons can grow to fit their text.
local function wrapMeasuredText(text, maxWidth, size, font)
    local lines = {}
    size = math.max(1, tonumber(size) or 1)
    maxWidth = math.max(1, tonumber(maxWidth) or 1)
    font = font or F.regular
    local measureOptions = { font = font, size = size }
    local function measured(value)
        return draw.measure_text(value, measureOptions)
    end

    forEachLiteralLine(text, function(sourceLine)
        local line = ""
        local function appendWord(word)
            local candidate = line == "" and word or (line .. " " .. word)
            if measured(candidate) <= maxWidth then
                line = candidate
                return
            end

            if line ~= "" then
                table.insert(lines, line)
                line = ""
            end

            -- A single long path, URL, or error token must not escape the menu.
            -- Split it only at UTF-8 code-point boundaries, never by raw byte.
            local chunk = ""
            local index = 1
            while index <= #word do
                local first = string.byte(word, index)
                local length = first < 128 and 1 or first < 224 and 2 or first < 240 and 3 or 4
                local glyph = string.sub(word, index, math.min(#word, index + length - 1))
                if chunk ~= "" and measured(chunk .. glyph) > maxWidth then
                    table.insert(lines, chunk)
                    chunk = glyph
                else
                    chunk = chunk .. glyph
                end
                index = index + length
            end
            line = chunk
        end

        forEachWord(sourceLine, appendWord)
        table.insert(lines, line)
    end)
    if #lines == 0 then lines[1] = "" end
    return lines
end

-- Editable metadata values use wrapped lines too. Keep the original UTF-8 byte
-- ranges beside each line so the caret and selection can follow text onto later
-- lines without changing the underlying string or its keyboard editing indices.
local function wrapEditableText(text, maxWidth, size, font)
    text = tostring(text or "")
    size = math.max(1, tonumber(size) or 1)
    maxWidth = math.max(1, tonumber(maxWidth) or 1)
    font = font or F.regular
    local measureOptions = { font = font, size = size }
    local function measured(value)
        return draw.measure_text(value, measureOptions)
    end
    local function glyphEnd(source, index)
        local first = string.byte(source, index)
        local length = first < 128 and 1 or first < 224 and 2 or first < 240 and 3 or 4
        return math.min(#source + 1, index + length)
    end
    local function isSpaceGlyph(glyph)
        return glyph == " " or glyph == "\t" or glyph == "\r" or glyph == "\v" or glyph == "\f"
    end

    local lines = {}
    local function addLine(startByte, endByte, nextByte)
        table.insert(lines, {
            text = string.sub(text, startByte, endByte - 1),
            start_byte = startByte,
            end_byte = endByte,
            next_byte = nextByte,
        })
    end
    local function wrapParagraph(startByte, endByte)
        if startByte >= endByte then
            addLine(startByte, startByte, startByte)
            return
        end
        local lineStart = startByte
        while lineStart < endByte do
            local cursor = lineStart
            local fitEnd = lineStart
            local whitespaceStart, whitespaceEnd = nil, nil
            local overflow = false
            while cursor < endByte do
                local nextByte = math.min(endByte, glyphEnd(text, cursor))
                local glyph = string.sub(text, cursor, nextByte - 1)
                local candidate = string.sub(text, lineStart, nextByte - 1)
                if measured(candidate) <= maxWidth or fitEnd == lineStart then
                    fitEnd = nextByte
                    if isSpaceGlyph(glyph) then
                        whitespaceStart, whitespaceEnd = cursor, nextByte
                    end
                    cursor = nextByte
                else
                    overflow = true
                    break
                end
            end

            if not overflow then
                addLine(lineStart, endByte, endByte)
                break
            end

            local lineEnd, nextStart
            if whitespaceStart ~= nil and whitespaceStart > lineStart then
                lineEnd = whitespaceStart
                nextStart = whitespaceEnd
                while nextStart < endByte do
                    local nextByte = math.min(endByte, glyphEnd(text, nextStart))
                    local glyph = string.sub(text, nextStart, nextByte - 1)
                    if not isSpaceGlyph(glyph) then break end
                    nextStart = nextByte
                end
            else
                lineEnd = fitEnd
                nextStart = fitEnd
            end
            if lineEnd <= lineStart then
                lineEnd = math.max(fitEnd, glyphEnd(text, lineStart))
                nextStart = lineEnd
            end
            addLine(lineStart, lineEnd, nextStart)
            lineStart = nextStart
        end
    end

    local paragraphStart = 1
    while true do
        local newline = string.find(text, "\n", paragraphStart, true)
        local paragraphEnd = newline or (#text + 1)
        wrapParagraph(paragraphStart, paragraphEnd)
        if newline == nil then break end
        paragraphStart = newline + 1
        if paragraphStart > #text then
            wrapParagraph(paragraphStart, paragraphStart)
            break
        end
    end
    if #lines == 0 then addLine(1, 1, 1) end
    return lines
end

local function panel(id, x, y, width, height, title, layer)
    ui.button({ id = id .. "_shield", x = x, y = y, width = width, height = height,
        action = "editor.block" })
    UI.draw_panel({
        id = id, x = x, y = y, width = width, height = height,
        color = C.panel, layer = layer or 1,
    })
    if title and title ~= "" then
        draw.text(title, {
            parent = id, x = 14, y = 10, size = fontSize("heading"),
            color = C.text, font = F.regular, anchor = "top-left",
            layer = (layer or 1) + 1,
        })
        draw.rect({
            parent = id, x = 12, y = 42, width = math.max(1, width - 24), height = 1,
            color = C.hover, layer = (layer or 1) + 1,
        })
    end
end

local function label(text, x, y, size, color, parent, layer)
    draw.text(tostring(text or ""), {
        parent = parent, x = x, y = y, size = size or fontSize("body"),
        color = color or C.text, font = F.regular, anchor = "top-left",
        layer = layer or 10,
    })
end

local function button(item, x, y, width, height, options)
    if item == nil then return end
    options = options or {}
    local disabled = item.enabled == false
    local selected = item.selected == true
    local visualSelected = selected
    if options.selected_visual ~= nil then visualSelected = options.selected_visual end
    if disabled then
        draw.rect({
            id = item.id .. "_disabled", x = x, y = y, width = width, height = height,
            color = C.button, alpha = 0.75, layer = options.layer or 4,
        })
        draw.text(tostring(item.label or ""), {
            x = x + width * 0.5, y = y + height * 0.5,
            size = options.text_size or fontSize("body"), color = C.muted,
            font = F.regular, anchor = "center", layer = (options.layer or 4) + 1,
        })
        return
    end
    UI.draw_button({
        id = item.id, x = x, y = y, width = width, height = height,
        label = item.label, action = item.action, value = item.value,
        selected = visualSelected,
        variant = options.variant or item.variant or (selected and "primary" or "secondary"),
        hit_test_priority = options.hit_test_priority,
        accent = options.accent ~= false, accent_color = options.accent_color or item.element_color,
        accent_hover_color = options.accent_hover_color,
        background_color = options.background_color,
        accent_width = options.accent_width or 3,
        font = F.medium, text_size = options.text_size or fontSize("body"),
        text_color = options.text_color, layer = options.layer or 4,
    })
end

local function shield(id, x, y, width, height, layer)
    ui.button({ id = id, x = x, y = y, width = width, height = height,
        action = "editor.block" })
    draw.rect({ x = x, y = y, width = width, height = height, color = C.panel,
        alpha = 0.97, layer = layer or 1 })
end

local function drawActionRail(items, x, y, width, layer)
    if items == nil or #items == 0 then return end
    local cols = 1
    local gap = 8 * ACTIVE_SCALE
    local buttonH = 40 * ACTIVE_SCALE
    local buttonW = math.max(1, (width - gap) / cols)
    local panelH = math.ceil(#items / cols) * (buttonH + gap) + 12 * ACTIVE_SCALE
    panel("editor_action_rail", x, y, width, panelH, nil, layer)
    for index, item in ipairs(items) do
        local row = math.floor((index - 1) / cols)
        local col = (index - 1) % cols
        button(item, x + col * (buttonW + gap), y + 6 * ACTIVE_SCALE + row * (buttonH + gap), buttonW, buttonH,
            { layer = (layer or 4) + 2, text_size = fontSize("body") })
    end
end

local function drawBankButtons(items, x, y, width, layer)
    for index, item in ipairs(items or {}) do
        local bx = num(item.x, x)
        local by = num(item.y, y)
        local bw = num(item.width, 62 * ACTIVE_SCALE)
        local bh = num(item.height, 32 * ACTIVE_SCALE)
        local variant = item.variant or (item.selected and "primary" or "secondary")
        UI.draw_button({ id = item.id, x = bx, y = by, width = bw, height = bh,
            label = item.label, action = item.action, value = item.value,
            selected = item.selected == true, variant = variant, font = F.medium, text_size = fontSize("body"),
            accent = variant == "secondary", accent_color = item.selected and C.accent or C.accent_dim,
            layer = (layer or 4) + 2 })
    end
end

-- The toolbar renderer is declared before the input helper, so declare it here
-- to ensure its closure captures the local function rather than a nil global.
local drawTextField

local function drawToolbar(state, screenWidth, x, stripY, width, layer)
    local isPanels = state.panels_view == true
    if isPanels then
        local panelsWidth = math.min(width, LAYOUT.panels_toolbar_width * ACTIVE_SCALE,
            screenWidth * 0.28)
        x = x + width - panelsWidth
        width = panelsWidth
    end
    local menuOpen = state.section ~= "none" and state.section ~= "snap" and state.section ~= "metronome"
    local pad = 8 * ACTIVE_SCALE
    local gap = 5 * ACTIVE_SCALE
    local tabH = 34 * ACTIVE_SCALE
    local panelTabH = 32 * ACTIVE_SCALE
    local panelTabGap = 4 * ACTIVE_SCALE
    local stripH = isPanels
        and (16 * ACTIVE_SCALE + panelTabH * 3 + panelTabGap * 2)
        or (pad * 2 + tabH * 4 + gap * 3)
    local tabs = state.tabs or {}

    -- The bottom toolbar spans the window. This category strip sits just above
    -- it; each expanded menu opens beside the category that owns it.
    panel("editor_toolbar", x, stripY, width, stripH, nil, layer)
    if not isPanels then
        for i, item in ipairs(tabs) do
            button(item, x + pad, stripY + pad + (i - 1) * (tabH + gap),
                width - pad * 2, tabH,
                { layer = (layer or 4) + 2, text_size = fontSize("body") })
        end
    else
        local panelTabs = { state.panel_connector, state.panel_duration, state.panel_select }
        for index, item in ipairs(panelTabs) do
            button(item, x + pad, stripY + 8 * ACTIVE_SCALE + (index - 1) * (panelTabH + panelTabGap),
                width - pad * 2, panelTabH,
                { layer = (layer or 4) + 2, text_size = fontSize("body") })
        end
    end

    if isPanels and not menuOpen then
        local attrs = state.panel_attributes or {}
        local attrGap = 4 * ACTIVE_SCALE
        local attrH = 32 * ACTIVE_SCALE
        local rows = math.ceil(#attrs / 2)
        local attrPanelH = 48 * ACTIVE_SCALE + rows * (attrH + attrGap)
        local attrY = stripY - gap - attrPanelH
        -- The animations selector, the controls below it, and their child menus
        -- share the same dock width.
        local attrPanelW = width
        local attrX = x + width - attrPanelW
        panel("editor_panel_attributes", attrX, attrY, attrPanelW, attrPanelH, "ANIMATIONS", layer)
        local attrW = (attrPanelW - pad * 2 - attrGap) / 2
        for i, item in ipairs(attrs) do
            local row = math.floor((i - 1) / 2)
            local col = (i - 1) % 2
            UI.draw_button({ id = item.id,
                x = attrX + pad + col * (attrW + attrGap),
                y = attrY + 44 * ACTIVE_SCALE + row * (attrH + attrGap),
                width = attrW, height = attrH, label = item.label,
                action = item.action, value = item.value, selected = item.selected == true,
                variant = "attribute_" .. tostring(item.value), accent = false,
                text_color = "#FFFFFF", font = F.medium, text_size = fontSize("body"),
                layer = (layer or 4) + 2 })
        end
        return
    end

    if not menuOpen then return end
    local children = state.children or {}
        local isPanelsSingleColumn = isPanels
    local stutterMenu = state.section == "stutter"
    local menuPadding = (stutterMenu and 12 or
        (state.section == "bpm" or state.section == "select" or state.section == "duration") and 9 or 8) * ACTIVE_SCALE
    if isPanels and state.section == "duration" then menuPadding = 5 * ACTIVE_SCALE end
    local headerGap = (stutterMenu and 10 or
        (state.section == "bpm" or state.section == "select" or state.section == "duration") and 8 or 6) * ACTIVE_SCALE
    local menuHeaderH = 34 * ACTIVE_SCALE
    local closeButtonSize = 30 * ACTIVE_SCALE
    local menuMargin = LAYOUT.margin * ACTIVE_SCALE
    local desiredMenuWidth = num(state.menu_width, LAYOUT.right_dock_width) * ACTIVE_SCALE
    local availableMenuWidth = math.max(76 * ACTIVE_SCALE,
        (screenWidth - menuMargin * 2 - menuPadding * 2) * 0.46)
    local menuContentWidth = math.min(desiredMenuWidth, availableMenuWidth)
    local menuWidth = isPanels and width or menuContentWidth + menuPadding * 2
    local menuX = x - gap - menuWidth
    local dockBottom = stripY + stripH
    local function menuTopForHeight(menuH)
        local targetY
        if isPanels then
            local row = state.section == "duration" and 2 or 3
            targetY = stripY + 12 * ACTIVE_SCALE + (row - 1) * (panelTabH + panelTabGap)
        else
            local rootIndex = ({ notes = 1, tools = 2, bpm = 3, select = 4 })[state.root_section] or 1
            targetY = stripY + pad + (rootIndex - 1) * (tabH + gap)
        end
        return clamp(targetY, menuMargin, math.max(menuMargin, dockBottom - menuH))
    end

    if stutterMenu then
        local rowLabelH = 17 * ACTIVE_SCALE
        local rowLabelGap = 4 * ACTIVE_SCALE
        local childH = 38 * ACTIVE_SCALE
        local rowGap = 5 * ACTIVE_SCALE
        local rowInset = 6 * ACTIVE_SCALE
        local rowH = rowInset + rowLabelH + rowLabelGap + childH
        local menuH = #children > 0
            and (menuPadding * 2 + menuHeaderH + headerGap * 2 + rowH * 5 + rowGap * 5 + childH)
            or 56 * ACTIVE_SCALE
        local menuY = menuTopForHeight(menuH)
        panel("editor_toolbar_menu", menuX, menuY, menuWidth, menuH,
            state.menu_title, (layer or 4) + 1)
        UI.draw_button({ id = "editor_menu_close", x = menuX + menuWidth - 38 * ACTIVE_SCALE,
            y = menuY + 5 * ACTIVE_SCALE, width = closeButtonSize, height = closeButtonSize,
            label = "X", action = "editor.menu.close", variant = "danger", accent = false,
            font = F.medium, text_size = fontSize("body"), layer = (layer or 4) + 4 })
        if #children == 0 then return end

        local childByValue = {}
        local captionByValue = {}
        for i, item in ipairs(children) do
            local index = tonumber(item.value)
            if index ~= nil then
                childByValue[index] = item
                captionByValue[index] = (state.control_labels or {})[i]
            end
        end
        local childGap = 5 * ACTIVE_SCALE
        local innerX = menuX + menuPadding + rowInset + 2 * ACTIVE_SCALE
        local innerW = menuWidth - menuPadding * 2 - (rowInset + 2 * ACTIVE_SCALE) * 2
        local cellW = (innerW - childGap) / 2
        local firstRowY = menuY + menuPadding + menuHeaderH + headerGap
        local function drawCaptionedChild(index, x, y, w)
            local caption = captionByValue[index]
            local item = childByValue[index]
            if caption and caption ~= "" then
                label(caption, x + 2 * ACTIVE_SCALE, y + rowInset,
                    fontSize("small"), C.muted, nil, (layer or 4) + 3)
            end
            local variant = item and string.sub(tostring(item.label or ""), 1, 6) == "CANCEL" and "warning" or nil
            local isInput = (state.stutter_ramp == true and (index == 3 or index == 4)) or
                (state.stutter_ramp ~= true and index >= 3 and index <= 6)
            if isInput and item then
                local focused = state.stutter_input_focused == true and
                    tonumber(state.stutter_input_child) == index
                drawTextField(item.id, item.label, "editor.child", item.value,
                    x, y + rowInset + rowLabelH + rowLabelGap, w, childH, (layer or 4) + 3,
                    focused, true,
                    focused and state.stutter_input_caret_offset or 0,
                    focused and state.stutter_input_selection_offset or 0,
                    focused and state.stutter_input_selection_width or 0,
                    state.stutter_input_text_width, false)
            else
                button(item, x, y + rowInset + rowLabelH + rowLabelGap,
                    w, childH, { layer = (layer or 4) + 3, text_size = fontSize("body"), variant = variant })
            end
        end
        local rowValues = { { 0, 1, 0 }, { 3, 4, 2 }, { 5, 6, 3 }, { 7, 8, 4 } }
        for row = 1, 4 do
            local y = firstRowY + rowValues[row][3] * (rowH + rowGap)
            local pair = rowValues[row]
            drawCaptionedChild(pair[1], innerX, y, cellW)
            drawCaptionedChild(pair[2], innerX + cellW + childGap, y, cellW)
        end

        local patterns = state.stutter_patterns or {}
        local patternY = firstRowY + rowH + rowGap
        label("PATTERN", innerX + 2 * ACTIVE_SCALE, patternY + rowInset,
            fontSize("small"), C.muted, nil, (layer or 4) + 3)
        local patternGap = 4 * ACTIVE_SCALE
        local patternW = (innerW - patternGap * 2) / 3
        for i, item in ipairs(patterns) do
            button(item, innerX + (i - 1) * (patternW + patternGap),
                patternY + rowInset + rowLabelH + rowLabelGap, patternW, childH,
                { layer = (layer or 4) + 3, text_size = fontSize("body") })
        end

        local actionY = firstRowY + 5 * (rowH + rowGap)
        local actionGap = 5 * ACTIVE_SCALE
        local actionW = (innerW - actionGap * 2) / 3
        for i, index in ipairs({ 9, 10, 11 }) do
            local variant = index == 9 and "success" or index == 10 and "danger" or nil
            button(childByValue[index], innerX + (i - 1) * (actionW + actionGap), actionY,
                actionW, childH, { layer = (layer or 4) + 4, text_size = fontSize("body"),
                    variant = variant })
        end
        return
    end

    local cols = state.section == "superflick" and 3 or
        (isPanelsSingleColumn and 1 or 2)
    local rows = math.ceil(#children / cols)
    local childH = 38 * ACTIVE_SCALE
    local childGap = 5 * ACTIVE_SCALE
    local labelH = 17 * ACTIVE_SCALE
    local labelGap = 4 * ACTIVE_SCALE
    local captions = state.control_labels or {}
    local captionRows = {}
    for i, caption in ipairs(captions) do
        if caption ~= nil and caption ~= "" then
            captionRows[math.floor((i - 1) / cols)] = true
        end
    end
    local captionRowCount = 0
    for _ in pairs(captionRows) do captionRowCount = captionRowCount + 1 end
    local menuH = menuPadding * 2 + menuHeaderH + headerGap * 2 + rows * childH +
        captionRowCount * (labelH + labelGap) + math.max(0, rows - 1) * childGap
    local menuY = menuTopForHeight(menuH)
    panel("editor_toolbar_menu", menuX, menuY, menuWidth, menuH, state.menu_title, (layer or 4) + 1)
    UI.draw_button({ id = "editor_menu_close", x = menuX + menuWidth - 38 * ACTIVE_SCALE,
        y = menuY + 5 * ACTIVE_SCALE, width = closeButtonSize, height = closeButtonSize,
        label = "X", action = "editor.menu.close", variant = "danger", accent = false,
        font = F.medium, text_size = fontSize("body"), layer = (layer or 4) + 4 })
    local childPad = isPanelsSingleColumn and 6 * ACTIVE_SCALE or pad
    local childW = (menuWidth - childPad * 2 - (cols - 1) * childGap) / cols
    local childrenY = menuY + menuPadding + menuHeaderH + headerGap
    for i, item in ipairs(children) do
        local row = math.floor((i - 1) / cols)
        local col = (i - 1) % cols
        local bx = menuX + childPad + col * (childW + childGap)
        local spansFinalRow = cols > 1 and (#children % cols ~= 0) and i == #children
        if spansFinalRow then
            bx = menuX + childPad
            childW = menuWidth - childPad * 2
        end
        local by = childrenY
        for previousRow = 0, row - 1 do
            by = by + childH + childGap
            if captionRows[previousRow] then by = by + labelH + labelGap end
        end
        local caption = captions[i]
        if caption ~= nil and caption ~= "" then
            label(caption, bx + 2 * ACTIVE_SCALE, by + 2 * ACTIVE_SCALE,
                fontSize("small"), C.muted, nil, (layer or 4) + 3)
        end
        local fieldY = by + (caption ~= nil and caption ~= "" and labelH + labelGap or 0)
        if (state.section == "bpm" and i == 1) or (state.section == "duration" and i == 1) then
            local isBpm = state.section == "bpm"
            local focused = isBpm and state.bpm_input_focused == true or
                (not isBpm and state.duration_input_focused == true)
            drawTextField(item.id, item.label, "editor.child", item.value,
                bx, fieldY, childW, childH, (layer or 4) + 3,
                focused, true,
                isBpm and state.bpm_input_caret_offset or state.duration_input_caret_offset,
                isBpm and state.bpm_input_selection_offset or state.duration_input_selection_offset,
                isBpm and state.bpm_input_selection_width or state.duration_input_selection_width,
                isBpm and state.bpm_input_text_width or state.duration_input_text_width,
                false)
        else
            button(item, bx, fieldY, childW, childH,
                { layer = (layer or 4) + 3, text_size = fontSize("body") })
        end
    end
end

local function drawProblems(state, x, y, width, layer)
    local rows = state.problems or {}
    local totalRows = num(state.problems_count, #rows)
    local expanded = state.problems_expanded == true
    local rowCount = math.min(LAYOUT.problem_rows, totalRows)
    local problemRowHeight = 40 * ACTIVE_SCALE
    local rowGap = 4 * ACTIVE_SCALE
    local collapsedHeight = 52 * ACTIVE_SCALE
    local h = expanded and (104 * ACTIVE_SCALE + rowCount * (problemRowHeight + rowGap)) or collapsedHeight
    panel(expanded and "editor_problems_expanded" or "editor_problems_collapsed", x, y, width, h,
        nil, layer)
    local summaryY = y + collapsedHeight * 0.5
    local summaryWidth = width - (totalRows > 0 and 210 or 84) * ACTIVE_SCALE
    draw.text(fittedText(state.problems_summary, summaryWidth, fontSize("body")), {
        x = x + 14 * ACTIVE_SCALE, y = summaryY, size = fontSize("body"),
        color = state.problems_errors > 0 and C.error or state.problems_warnings > 0 and C.accent or C.text,
        font = F.regular, anchor = "left", layer = (layer or 4) + 2,
    })
    UI.draw_button({ id = "editor_problems_toggle", x = x + width - 40 * ACTIVE_SCALE, y = y + 8 * ACTIVE_SCALE,
        width = 32 * ACTIVE_SCALE, height = 36 * ACTIVE_SCALE, label = expanded and "−" or "+",
        action = "editor.problems.toggle", font = F.medium, text_size = fontSize("body"),
        layer = (layer or 4) + 3 })
    if totalRows > 0 then
        UI.draw_button({ id = "editor_problems_fix", x = x + width - 166 * ACTIVE_SCALE, y = y + 8 * ACTIVE_SCALE,
            width = 122 * ACTIVE_SCALE, height = 36 * ACTIVE_SCALE, label = "QUICK FIX", action = "editor.problems.quick_fix",
            font = F.medium, text_size = fontSize("small"), layer = (layer or 4) + 3 })
    end
    if expanded then
        for slot = 1, rowCount do
            local issue = rows[slot]
            if issue then
                local rowX = x + 10 * ACTIVE_SCALE
                local rowY = y + 94 * ACTIVE_SCALE + (slot - 1) * (problemRowHeight + rowGap)
                local rowW = width - 38 * ACTIVE_SCALE
                UI.draw_button({ id = "editor_problem_" .. tostring(issue.index),
                    x = rowX, y = rowY, width = rowW, height = problemRowHeight,
                    action = "editor.problems.row", value = issue.index, label = "",
                    variant = "secondary", accent = true,
                    accent_color = issue.warning and C.accent or C.error, layer = (layer or 4) + 4 })
                draw.text(fittedMeasuredText(issue.label, rowW - 16 * ACTIVE_SCALE,
                    fontSize("small"), F.regular), {
                    x = rowX + 8 * ACTIVE_SCALE, y = rowY + problemRowHeight * 0.5,
                    size = fontSize("small"), color = C.text, font = F.regular,
                    anchor = "left", layer = (layer or 4) + 6,
                })
            end
        end
        if num(state.problems_scroll_max, 0) > 0 then
            local barX = x + width - 18 * ACTIVE_SCALE
            local barY = y + 94 * ACTIVE_SCALE
            local barH = math.max(1, rowCount * (problemRowHeight + rowGap) - rowGap)
            local scrollMax = num(state.problems_scroll_max, 1)
            local thumbH = math.max(24 * ACTIVE_SCALE, barH * rowCount / (rowCount + scrollMax))
            local thumbTravel = math.max(0, barH - thumbH)
            local thumbY = barY + thumbTravel * num(state.problems_scroll, 0) / scrollMax
            draw.rect({ x = barX, y = barY, width = 6 * ACTIVE_SCALE, height = barH,
                color = C.button, layer = (layer or 4) + 4 })
            draw.rect({ x = barX, y = thumbY, width = 6 * ACTIVE_SCALE, height = thumbH,
                color = C.accent, layer = (layer or 4) + 5 })
            ui.slider({ id = "editor_problems_scrollbar", x = barX - 6 * ACTIVE_SCALE, y = barY,
                width = 18 * ACTIVE_SCALE, height = barH, min = 0, max = scrollMax, step = 1,
                axis = "vertical", action = "editor.problems.scroll_to" })
        end
    end
end

local function drawTimeline(state, x, y, width, height, layer)
    local duration = math.max(0.001, num(state.duration_seconds, 1))
    local current = clamp(num(state.play_seconds, 0) / duration, 0, 1)
    local currentSeconds = current * duration
    local trackX = x + 7 * ACTIVE_SCALE
    local traceRight = x + width - 4 * ACTIVE_SCALE
    local top = y + 12 * ACTIVE_SCALE
    local bottom = y + height - 4 * ACTIVE_SCALE
    local trackHeight = math.max(1, bottom - top)
    local routeWidth = math.max(1, 1.2 * ACTIVE_SCALE)
    draw.rect({ x = x, y = y, width = width, height = height,
        color = { r = 12, g = 12, b = 14, a = 150 }, layer = layer or 3 })
    local function timeY(seconds)
        return bottom - clamp(num(seconds, 0) / duration, 0, 1) * trackHeight
    end
    local function traceX(index)
        index = clamp(num(index, 0), 0, 5)
        local side = index < 3 and "left" or "right"
        local localSlot = index % 3
        local innerLeft = trackX + 4 * ACTIVE_SCALE
        local innerRight = traceRight - 4 * ACTIVE_SCALE
        local sideGap = 5 * ACTIVE_SCALE
        local sideWidth = math.max(1, (innerRight - innerLeft - sideGap) * 0.5)
        local groupStart = side == "left" and innerLeft or (innerLeft + sideWidth + sideGap)
        return groupStart + localSlot * sideWidth * 0.5
    end
    local function segment(x1, seconds1, x2, seconds2, color, layerOffset)
        draw.line({ start = { x = x1, y = timeY(seconds1) },
            finish = { x = x2, y = timeY(seconds2) }, width = routeWidth,
            color = color, layer = (layer or 3) + (layerOffset or 3) })
    end
    local function routeRange(route, rangeStart, rangeEnd, color, onlySide)
        route = route or {}
        if #route == 0 or rangeEnd <= rangeStart then return end
        for index, point in ipairs(route) do
            local nextPoint = route[index + 1]
            local startSeconds = math.max(rangeStart, clamp(num(point.seconds, 0), 0, duration))
            local endSeconds = math.min(rangeEnd,
                clamp(nextPoint and num(nextPoint.seconds, duration) or duration, 0, duration))
            if endSeconds > startSeconds then
                if onlySide == nil or onlySide == "left" then
                    segment(traceX(point.left), startSeconds, traceX(point.left), endSeconds, color)
                end
                if onlySide == nil or onlySide == "right" then
                    segment(traceX(point.right), startSeconds, traceX(point.right), endSeconds, color)
                end
            end
            if nextPoint then
                local transition = num(nextPoint.seconds, 0)
                if transition >= rangeStart and transition <= rangeEnd then
                    if onlySide == nil or onlySide == "left" then
                        segment(traceX(point.left), transition, traceX(nextPoint.left), transition, color)
                    end
                    if onlySide == nil or onlySide == "right" then
                        segment(traceX(point.right), transition, traceX(nextPoint.right), transition, color)
                    end
                end
            end
        end
    end
    local function routeStateAt(route, seconds)
        local result = route and route[1]
        for _, point in ipairs(route or {}) do
            if num(point.seconds, 0) <= seconds + 0.0001 then result = point else break end
        end
        return result or { left = 1, right = 4 }
    end
    local function sameSide(a, b, side)
        return a ~= nil and b ~= nil and num(a[side], -1) == num(b[side], -2)
    end

    -- Permanent six-bank rails sit below the colored authored and preview routes.
    for rail = 0, 5 do
        local railX = traceX(rail)
        draw.line({ start = { x = railX, y = timeY(0) },
            finish = { x = railX, y = timeY(duration) }, width = 2.4 * ACTIVE_SCALE,
            color = C.button, layer = (layer or 3) + 1 })
    end
    local intended = state.timeline_intended_route or {}
    local actual = state.timeline_actual_route or {}
    local forecast = state.timeline_forecast_route or {}
    local progressColor = "#34D399"
    local forecastColor = "#F5F8FC"
    local alternateColor = "#F84652"
    local intendedFutureColor = "#F59E0B"
    routeRange(intended, 0, currentSeconds, progressColor, nil)

    local currentActual = routeStateAt(actual, currentSeconds)
    local currentIntended = routeStateAt(intended, currentSeconds)
    local boundaries = { 0, currentSeconds }
    for _, route in ipairs({ actual, intended }) do
        for _, point in ipairs(route) do
            local seconds = num(point.seconds, -1)
            if seconds > 0 and seconds < currentSeconds then
                table.insert(boundaries, seconds)
            end
        end
    end
    table.sort(boundaries)
    local uniqueBoundaries = {}
    for _, seconds in ipairs(boundaries) do
        if #uniqueBoundaries == 0 or math.abs(seconds - uniqueBoundaries[#uniqueBoundaries]) > 0.0001 then
            table.insert(uniqueBoundaries, seconds)
        end
    end
    for _, side in ipairs({ "left", "right" }) do
        if not sameSide(currentActual, currentIntended, side) then
            local lastMatch = 0
            for _, seconds in ipairs(uniqueBoundaries) do
                if sameSide(routeStateAt(actual, seconds), routeStateAt(intended, seconds), side) then
                    lastMatch = seconds
                end
            end
            for i = 1, #uniqueBoundaries - 1 do
                local startSeconds = uniqueBoundaries[i]
                local endSeconds = uniqueBoundaries[i + 1]
                local actualAtStart = routeStateAt(actual, startSeconds)
                local intendedAtStart = routeStateAt(intended, startSeconds)
                if endSeconds > lastMatch + 0.0001 and endSeconds > startSeconds and
                    not sameSide(actualAtStart, intendedAtStart, side) then
                    segment(traceX(actualAtStart[side]), math.max(startSeconds, lastMatch),
                        traceX(actualAtStart[side]), endSeconds, alternateColor)
                end
                local actualAtEnd = routeStateAt(actual, endSeconds)
                if endSeconds > lastMatch + 0.0001 and
                    (not sameSide(actualAtStart, intendedAtStart, side) or
                     not sameSide(actualAtEnd, routeStateAt(intended, endSeconds), side)) then
                    segment(traceX(actualAtStart[side]), endSeconds, traceX(actualAtEnd[side]), endSeconds, alternateColor)
                end
            end
            routeRange(intended, currentSeconds, duration, intendedFutureColor, side)
        end
    end
    routeRange(forecast, currentSeconds, duration, forecastColor, nil)

    local marks = state.timeline_marks or {}
    for i = 1, 5 do
        local ratio = (i - 1) / 4
        local markY = bottom - ratio * trackHeight
        draw.rect({ x = trackX, y = markY, width = traceRight - trackX,
            height = math.max(1, ACTIVE_SCALE), color = C.muted,
            alpha = (i == 1 or i == 5) and 0.35 or 0.18,
            layer = (layer or 3) + 6 })
        draw.text(marks[i] or "00:00.000", {
            x = x + width * 0.5, y = markY, size = fontSize("small"),
            color = (i == 1 or i == 5) and C.text or C.muted, font = F.regular,
            anchor = "center", layer = (layer or 3) + 7,
        })
    end
    local playY = timeY(currentSeconds)
    draw.rect({ x = x, y = playY - 2 * ACTIVE_SCALE, width = width,
        height = 4 * ACTIVE_SCALE, color = "#34D399", alpha = 0.65, layer = (layer or 3) + 8 })
    ui.slider({ id = "editor_timeline", x = x, y = y, width = width, height = height,
        min = 0, max = duration, step = 0, axis = "vertical", reverse = 1, action = "editor.seek" })
end

local function bottomActionSlots(items, cols)
    local list = items or {}
    local slots = {}
    for index, item in ipairs(list) do
        slots[index] = { item = item, row = 0, col = index - 1 }
    end
    return slots, #list > 0 and 1 or 0
end

local function drawBottomActionButton(item, x, y, width, height, layer)
    if item == nil then return end
    -- The editor actions share this row, so fit labels to each button rather than
    -- letting long names spill into neighboring controls at narrow resolutions.
    local textSize = math.min(fontSize("body"), math.max(fontSize("small") * 0.9, width * 0.12))
    local fittedLabel = fittedMeasuredText(item.label or "", math.max(1, width - 18 * ACTIVE_SCALE), textSize, F.medium)
    local drawItem = item
    if fittedLabel ~= tostring(item.label or "") then
        drawItem = {}
        for key, value in pairs(item) do drawItem[key] = value end
        drawItem.label = fittedLabel
    end
    button(drawItem, x, y, width, height, { layer = layer or 5, text_size = textSize })
end

local function drawBottomActions(items, x, y, width, layer, cols)
    cols = cols or LAYOUT.bottom_action_columns
    local gap = LAYOUT.bottom_action_gap * ACTIVE_SCALE
    local buttonH = LAYOUT.bottom_action_height * ACTIVE_SCALE
    local itemW = (width - gap * (cols - 1)) / cols
    local slots = bottomActionSlots(items, cols)
    for _, slot in ipairs(slots) do
        drawBottomActionButton(slot.item, x + slot.col * (itemW + gap),
            y + slot.row * (buttonH + gap), itemW, buttonH, layer or 5)
    end
end

-- Match settingsMenu's editable value fields: keep the uiStyle field surface,
-- but draw the edited text, selection, and caret separately so the caret does
-- not shift the value while the user types.
drawTextField = function(id, value, action, actionValue, x, y, width, height, layer, focused, editable,
    caretOffset, selectionOffset, selectionWidth, measuredTextWidth, centered, wrapValue, caretByteIndex, anchorByteIndex)
    local display = tostring(value or "")
    local textLeft = x + 10 * ACTIVE_SCALE
    local textLimit = math.max(1, width - 20 * ACTIVE_SCALE)
    local textSize = fontSize("small")
    local lineHeight = math.max(16 * ACTIVE_SCALE, textSize * 1.2)
    if not editable then
        draw.rect({ id = id .. "_readonly", x = x, y = y, width = width, height = height,
            color = C.button, layer = layer })
        if wrapValue then
            local lines = wrapMeasuredText(display, textLimit, textSize, F.regular)
            local blockHeight = #lines * lineHeight
            local lineY = y + (height - blockHeight) * 0.5
            for _, line in ipairs(lines) do
                draw.text(line, { x = textLeft, y = lineY, size = textSize, color = C.muted,
                    font = F.regular, anchor = "top-left", layer = layer + 2 })
                lineY = lineY + lineHeight
            end
        else
            draw.text(display, {
                x = textLeft, y = y + height * 0.5, size = textSize, color = C.muted,
                font = F.regular, anchor = "left", layer = layer + 2,
            })
        end
        return
    end
    UI.draw_button({
        id = id, x = x, y = y, width = width, height = height,
        label = "",
        text_size = textSize, text_color = C.text,
        font = F.medium, background_color = focused and C.selected or C.button,
        accent = focused, selected = focused,
        action = action, value = actionValue, layer = layer,
    })

    if wrapValue and focused then
        local lines = wrapEditableText(display, textLimit, textSize, F.regular)
        local blockHeight = #lines * lineHeight
        local lineY = y + (height - blockHeight) * 0.5
        local caretByte = clamp(num(caretByteIndex, #display), 0, #display)
        local anchorByte = clamp(num(anchorByteIndex, caretByte), 0, #display)
        local selectionStart = math.min(caretByte, anchorByte)
        local selectionEnd = math.max(caretByte, anchorByte)
        local caretLine, caretLocalByte = nil, 0
        local textHeight = math.min(textSize, math.max(1, height - 8 * ACTIVE_SCALE))

        for lineIndex, line in ipairs(lines) do
            local lineStart = line.start_byte - 1
            local lineEnd = line.end_byte - 1
            local nextStart = line.next_byte - 1
            local currentY = lineY + (lineIndex - 1) * lineHeight
            local visibleSelectionStart = math.max(selectionStart, lineStart)
            local visibleSelectionEnd = math.min(selectionEnd, lineEnd)
            if visibleSelectionEnd > visibleSelectionStart then
                local prefix = string.sub(display, line.start_byte, visibleSelectionStart)
                local selected = string.sub(display, visibleSelectionStart + 1, visibleSelectionEnd)
                local prefixWidth = draw.measure_text(prefix, { font = F.regular, size = textSize })
                local selectedWidth = draw.measure_text(selected, { font = F.regular, size = textSize })
                draw.rect({ x = textLeft + prefixWidth, y = currentY + (lineHeight - textHeight) * 0.5,
                    width = math.max(1, selectedWidth), height = textHeight,
                    color = C.accent_dim, layer = layer + 1 })
            end

            draw.text(line.text, { x = textLeft, y = currentY, size = textSize, color = C.text,
                font = F.regular, anchor = "top-left", layer = layer + 2 })
            if caretByte >= lineStart and caretByte < nextStart then
                caretLine = lineIndex
                caretLocalByte = math.max(0, math.min(caretByte, lineEnd) - lineStart)
            end
        end

        if caretLine == nil then
            caretLine = #lines
            local last = lines[caretLine]
            caretLocalByte = math.max(0, math.min(caretByte, last.end_byte - 1) - (last.start_byte - 1))
        end
        local caretText = lines[caretLine].text
        local caretPrefix = string.sub(caretText, 1, caretLocalByte)
        local caretX = textLeft + draw.measure_text(caretPrefix, { font = F.regular, size = textSize })
        local caretY = lineY + (caretLine - 1) * lineHeight + (lineHeight - textHeight) * 0.5
        draw.rect({ x = caretX, y = caretY, width = 1.5 * ACTIVE_SCALE, height = textHeight,
            color = C.text, layer = layer + 3 })
        return
    end

    if not focused and wrapValue then
        local lines = wrapMeasuredText(display, textLimit, textSize, F.regular)
        local blockHeight = #lines * lineHeight
        local lineY = y + (height - blockHeight) * 0.5
        for _, line in ipairs(lines) do
            draw.text(line, { x = textLeft, y = lineY, size = textSize, color = C.text,
                font = F.regular, anchor = "top-left", layer = layer + 2 })
            lineY = lineY + lineHeight
        end
        return
    end

    measuredTextWidth = num(measuredTextWidth, 0)
    if measuredTextWidth <= 0 then
        measuredTextWidth = draw.measure_text(display, { font = F.regular, size = textSize })
    end
    measuredTextWidth = math.min(textLimit, math.max(0, measuredTextWidth))
    if centered then textLeft = x + (width - measuredTextWidth) * 0.5 end
    selectionOffset = num(selectionOffset, 0)
    selectionWidth = num(selectionWidth, 0)
    selectionOffset = clamp(selectionOffset, 0, textLimit)
    selectionWidth = clamp(selectionWidth, 0, textLimit - selectionOffset)
    local textHeight = math.min(textSize, math.max(1, height - 8 * ACTIVE_SCALE))
    local textY = y + height * 0.5
    if focused and selectionWidth > 0 then
        draw.rect({ x = textLeft + selectionOffset, y = textY - textHeight * 0.5,
            width = math.max(1, selectionWidth), height = textHeight,
            color = C.accent_dim, layer = layer + 1 })
    end
    draw.text(display, {
        x = textLeft, y = textY,
        size = textSize, color = C.text,
        font = F.regular, anchor = "left", layer = layer + 2,
    })
    if focused then
        local fallbackCaret = measuredTextWidth
        local caretX = textLeft + clamp(num(caretOffset, fallbackCaret), 0, textLimit)
        draw.rect({ x = caretX, y = textY - textHeight * 0.5,
            width = 1.5 * ACTIVE_SCALE, height = textHeight,
            color = C.text, layer = layer + 3 })
    end
end

local function textField(item, x, y, width, height, layer, wrapValue)
    drawTextField("editor_meta_row_" .. tostring(item.index), item.value,
        item.editable and "editor.meta.row" or "editor.block", item.index,
        x, y, width, height, layer, item.editing == true, item.editable == true,
        item.caret_offset, item.selection_offset, item.selection_width, item.text_width, false, wrapValue == true,
        item.caret_byte_index, item.anchor_byte_index)
end

local function getMetaRowLayout(item, panelW, padding, scale, rowLabelH, rowLabelGap, rowGap)
    local trailing = item.label == "PREVIEW" and (132 * scale) or
        (item.label == "ALBUM" or
            (item.label == "ALBUM BIG" and item.value ~= "Unsupported by TSTS")) and (68 * scale) or (8 * scale)
    local fieldW = math.max(96 * scale, panelW - padding * 2 - trailing - 22 * scale)
    local fieldH = 46 * scale
    if item.kind ~= "toggle" and item.kind ~= "picker" and item.kind ~= "danger" then
        local textSize = fontSize("small")
        local lineHeight = math.max(16 * scale, textSize * 1.2)
        local lines = item.editing == true
            and wrapEditableText(item.value or "", math.max(1, fieldW - 20 * scale), textSize, F.regular)
            or wrapMeasuredText(item.value or "", math.max(1, fieldW - 20 * scale), textSize, F.regular)
        fieldH = math.ceil(math.max(fieldH, #lines * lineHeight + 12 * scale))
    end
    return {
        fieldW = fieldW,
        fieldH = fieldH,
        labelH = rowLabelH,
        labelGap = rowLabelGap,
        rowGap = rowGap,
        pitch = rowLabelH + rowLabelGap + fieldH + rowGap,
    }
end

local function buildMetaRowLayouts(rows, panelW, padding, scale, rowLabelH, rowLabelGap, rowGap)
    local layouts = {}
    local totalHeight = 0
    for index, item in ipairs(rows) do
        local layout = getMetaRowLayout(item, panelW, padding, scale, rowLabelH, rowLabelGap, rowGap)
        layouts[index] = layout
        totalHeight = totalHeight + layout.pitch
    end
    return layouts, totalHeight
end

local function metaPrefixHeight(layouts, endExclusive)
    local height = 0
    for index = 1, math.min(endExclusive or 0, #layouts) do
        height = height + layouts[index].pitch
    end
    return height
end

local function metaMaxScrollStart(layouts, contentHeight)
    local suffixHeight = 0
    for index = #layouts, 1, -1 do
        local pitch = layouts[index].pitch
        if suffixHeight + pitch > contentHeight then
            return math.max(0, suffixHeight == 0 and index - 1 or index)
        end
        suffixHeight = suffixHeight + pitch
    end
    return 0
end

local function drawMeta(state, w, h, layer)
    if state.meta_open ~= true then return end
    local scale = ACTIVE_SCALE
    local margin = num(state.meta_panel_margin, 10) * scale
    local panelW = math.min(w - margin * 2,
        math.max(420 * scale, math.min(num(state.meta_panel_width, 660) * scale, 660 * scale, w * 0.60)))
    local panelH = math.max(1, h - margin * 2)
    local x = w - margin - panelW
    local y = margin
    panel("editor_meta", x, y, panelW, panelH, state.meta_title, (layer or 100) + 1)
    UI.draw_button({ id = "editor_meta_close", x = x + panelW - 52 * scale, y = y + 10 * scale,
        width = 42 * scale, height = 42 * scale, label = "X", action = "editor.meta.close", font = F.medium, text_size = fontSize("body"),
        variant = "danger", accent = false, layer = (layer or 100) + 4 })
    UI.draw_button({ id = "editor_meta_exit", x = x + panelW - 134 * scale, y = y + 16 * scale,
        width = 76 * scale, height = 30 * scale, label = "EXIT", action = "editor.meta.exit", font = F.medium, text_size = fontSize("body"),
        variant = "danger", accent = false, layer = (layer or 100) + 4 })

    local difficulties = state.meta_difficulties or {}
    local diffGap = 4 * scale
    local diffW = (panelW - 24 * scale - diffGap * (#difficulties - 1)) / math.max(1, #difficulties)
    for i, item in ipairs(difficulties) do
        button(item, x + 12 * scale + (i - 1) * (diffW + diffGap), y + 80 * scale, diffW, 30 * scale,
            { layer = (layer or 100) + 4, text_size = fontSize("body") })
    end

    local categories = state.meta_categories or {}
    local catGap = 4 * scale
    local catW = (panelW - 24 * scale - catGap * (#categories - 1)) / math.max(1, #categories)
    for i, item in ipairs(categories) do
        button(item, x + 12 * scale + (i - 1) * (catW + catGap), y + 118 * scale, catW, 28 * scale,
            { layer = (layer or 100) + 4, text_size = fontSize("body") })
    end

    local rows = state.meta_rows or {}
    local padding = 12 * scale
    local exportToggleH = 38 * scale
    local exportToggleY = y + panelH - padding - exportToggleH
    local difficultyToggleH = 34 * scale
    local difficultyToggleY = exportToggleY - 6 * scale - difficultyToggleH
    local contentBottom = state.meta_category == "TEST" and (y + panelH - padding) or (difficultyToggleY - padding)
    local contentTop = y + 156 * scale
    local rowLabelH = 20 * scale
    local rowLabelGap = 5 * scale
    local baseFieldH = 46 * scale
    local rowGap = 8 * scale
    local rowPitch = rowLabelH + rowLabelGap + baseFieldH + rowGap
    local dynamicLayouts, totalRowsHeight = buildMetaRowLayouts(rows, panelW, padding, scale,
        rowLabelH, rowLabelGap, rowGap)
    local contentHeight = math.max(0, contentBottom - contentTop)
    -- C# computes the same row heights for wheel input and passes the maximum
    -- scroll start through state, keeping wheel and scrollbar limits in sync.
    local maxScrollStart = clamp(num(state.meta_scroll_max,
        metaMaxScrollStart(dynamicLayouts, contentHeight)), 0, math.max(0, #rows - 1))
    local rowStart = clamp(num(state.meta_scroll, 0), 0, maxScrollStart)

    local function drawMetaRow(item, rowY, rowLayout)
        if not item then return end
        local wrapValue = rowLayout ~= nil
        if not rowLayout then
            local trailing = item.label == "PREVIEW" and (132 * scale) or
                (item.label == "ALBUM" or
                    (item.label == "ALBUM BIG" and item.value ~= "Unsupported by TSTS")) and (68 * scale) or (8 * scale)
            rowLayout = { fieldW = math.max(96 * scale, panelW - padding * 2 - trailing - 22 * scale), fieldH = baseFieldH }
        end
        label(item.label, x + padding, rowY, fontSize("small"), C.muted, nil, (layer or 100) + 4)
        local fieldY = rowY + rowLabelH + rowLabelGap
        local fieldX = x + padding
        local fieldW = rowLayout.fieldW
        local fieldH = rowLayout.fieldH
        if item.kind == "danger" then
            if state.meta_delete_enabled == true then
                UI.draw_button({ id = "editor_meta_delete_difficulty", x = fieldX, y = fieldY,
                    width = fieldW, height = fieldH, label = item.label,
                    action = "editor.meta.delete_confirm", variant = "danger", accent = false,
                    font = F.medium, text_size = fontSize("small"), layer = (layer or 100) + 8 })
            else
                draw.rect({ id = "editor_meta_delete_difficulty_disabled", x = fieldX, y = fieldY,
                    width = fieldW, height = fieldH, color = C.button, alpha = 0.65, layer = (layer or 100) + 8 })
                draw.text(item.label, { x = fieldX + fieldW * 0.5, y = fieldY + fieldH * 0.5,
                    size = fontSize("small"), color = C.muted, font = F.regular,
                    anchor = "center", layer = (layer or 100) + 9 })
            end
        elseif item.kind == "toggle" or item.kind == "picker" then
            local isElementPicker = item.label == "ELEMENT"
            local variant = isElementPicker and "secondary" or
                (item.variant or (item.selected and "primary" or "secondary"))
            UI.draw_button({ id = "editor_meta_row_" .. tostring(item.index),
                x = fieldX, y = fieldY, width = fieldW, height = fieldH,
                label = item.value, action = "editor.meta.row", value = item.index,
                -- Opening the element list must not turn this button into a danger,
                -- success, or info button. Keep the body neutral in every state.
                selected = (not isElementPicker) and item.selected == true,
                variant = variant, accent = isElementPicker or variant == "secondary",
                accent_color = isElementPicker and item.element_color or nil,
                accent_hover_color = isElementPicker and item.element_hover_color or nil,
                background_color = isElementPicker and C.button or nil,
                font = (not isElementPicker and item.selected) and F.medium or F.regular,
                text_size = fontSize("small"), layer = (layer or 100) + 5 })
        else
            textField(item, fieldX, fieldY, fieldW, fieldH, (layer or 100) + 5, wrapValue)
        end
        if item.label == "PREVIEW" then
            UI.draw_button({ id = "editor_meta_preview_edit", x = x + panelW - 130 * scale, y = fieldY,
                width = 58 * scale, height = fieldH, label = "EDIT",
                action = "editor.meta.preview_edit", font = F.medium, text_size = fontSize("small"),
                layer = (layer or 100) + 7 })
            UI.draw_button({ id = "editor_meta_preview_play", x = x + panelW - 64 * scale, y = fieldY,
                width = 58 * scale, height = fieldH, label = state.meta_preview_playing and "STOP" or "PLAY",
                action = "editor.meta.preview_play", font = F.medium, text_size = fontSize("small"),
                layer = (layer or 100) + 7 })
        elseif item.label == "ALBUM" then
            UI.draw_button({ id = "editor_meta_album", x = x + panelW - 70 * scale, y = fieldY,
                width = 58 * scale, height = fieldH, label = "PICK",
                action = "editor.meta.media_album", font = F.medium, text_size = fontSize("small"),
                layer = (layer or 100) + 7 })
        elseif item.label == "ALBUM BIG" and item.value ~= "Unsupported by TSTS" then
            UI.draw_button({ id = "editor_meta_album_big", x = x + panelW - 70 * scale, y = fieldY,
                width = 58 * scale, height = fieldH, label = "PICK",
                action = "editor.meta.media_album_big", font = F.medium, text_size = fontSize("small"),
                layer = (layer or 100) + 7 })
        end
    end

    if state.meta_category == "TEST" then
        local textWidth = math.max(1, panelW - padding * 2)
        local textX = x + padding
        local bodySize = fontSize("body")
        local smallSize = fontSize("small")
        local bodyLineHeight = math.max(18 * scale, bodySize * 1.35)
        local smallLineHeight = math.max(16 * scale, smallSize * 1.4)
        local bodyLayer = (layer or 100) + 5
        local labelLayer = (layer or 100) + 4
        local testButtonY = contentBottom - 38 * scale - 8 * scale
        local adbEnabled = state.meta_test_adb_enabled == true
        local textY = contentTop + 2 * scale

        local function drawWrappedValue(value, size, color, font, lineHeight, drawLayer)
            local wrapped = wrapMeasuredText(value, textWidth, size, font)
            for lineIndex, line in ipairs(wrapped) do
                draw.text(line, {
                    x = textX, y = textY + (lineIndex - 1) * lineHeight,
                    size = size, color = color, font = font,
                    anchor = "top-left", layer = drawLayer or bodyLayer })
            end
            textY = textY + #wrapped * lineHeight + 5 * scale
        end

        local function drawTestField(caption, value, color, font)
            label(caption, textX, textY, smallSize, C.muted, nil, labelLayer)
            textY = textY + math.max(17 * scale, smallSize * 1.3)
            drawWrappedValue(value, bodySize, color, font or F.regular, bodyLineHeight, bodyLayer)
        end

        label("LOCAL TEST", textX, contentTop + 2 * scale, fontSize("body"), C.text, nil, labelLayer + 1)
        local adbControlY = contentTop + 28 * scale
        UI.draw_button({ id = "editor_lan_adb_toggle", x = textX, y = adbControlY,
            width = math.max(1, panelW - padding * 2), height = 34 * scale,
            label = adbEnabled and "USE ADB: ON" or "USE ADB: OFF",
            action = "editor.lan_test.adb.toggle", enabled = state.meta_test_busy ~= true,
            variant = adbEnabled and "primary" or "secondary",
            font = F.medium, text_size = fontSize("small"), layer = bodyLayer + 2 })
        textY = adbControlY + 44 * scale

        drawTestField("STATUS", state.meta_test_status or "Not running",
            state.meta_test_running and C.accent or C.text, F.medium)
        local musicStatus = state.meta_test_music_available and "Ready" or "Missing — import music in MEDIA first"
        drawTestField("MUSIC", musicStatus,
            state.meta_test_music_available and C.text or C.error, F.regular)
        drawTestField("AVAILABLE CHARTS", state.meta_test_chart_summary or "No charts exported", C.text, F.regular)
        drawTestField(adbEnabled and "ADB ENDPOINT" or "LAN ADDRESS",
            (state.meta_test_address and state.meta_test_address ~= "" and state.meta_test_address) or (adbEnabled and "ADB forwarding is set up when a device connects" or "Shown after the server starts"),
            C.text, F.regular)
        if adbEnabled then
            drawTestField("ADB", state.meta_test_adb_status or "Starting", C.accent, F.medium)
            -- Local beatmap testing must be enabled in ReSONANCE for both ADB and LAN.
            -- Keep this notice in the ADB section of the actual Lua menu skin.
            drawWrappedValue("Enable Local beatmap testing in your ReSONANCE account settings.",
                smallSize, C.text, F.medium, smallLineHeight, bodyLayer)
            if state.meta_test_adb_details and state.meta_test_adb_details ~= "" then
                drawWrappedValue(state.meta_test_adb_details, smallSize, C.muted, F.regular, smallLineHeight, bodyLayer)
            end
            drawWrappedValue("USB: Enable Developer Options and USB debugging, connect your phone with a data-capable cable, then accept the debugging prompt.",
                smallSize, C.muted, F.regular, smallLineHeight, bodyLayer)
            local devices = state.meta_test_adb_devices or {}
            for i = 1, math.min(#devices, 2) do
                local entry = tostring(devices[i])
                local serial = string.match(entry, "^([^ ]+)") or ""
                local wireless = string.find(serial, ":", 1, true) ~= nil or
                    string.find(serial, "_adb-tls-connect._tcp", 1, true) ~= nil
                local rowY = textY
                draw.text(fittedMeasuredText(entry, textWidth - (wireless and 112 * scale or 8 * scale),
                    smallSize, F.regular), { x = textX, y = rowY + 16 * scale, size = smallSize,
                    color = C.text, font = F.regular, anchor = "left", layer = bodyLayer + 1 })
                if wireless then
                    UI.draw_button({ id = "editor_lan_adb_disconnect_" .. tostring(i),
                        x = textX + textWidth - 104 * scale, y = rowY + 2 * scale,
                        width = 104 * scale, height = 30 * scale, label = "DISCONNECT",
                        action = "editor.lan_test.adb.disconnect", value = serial,
                        variant = "danger", font = F.medium, text_size = fontSize("small"), layer = bodyLayer + 3 })
                end
                textY = textY + 34 * scale
            end
        end
        if state.meta_test_details and state.meta_test_details ~= "" then
            drawWrappedValue(state.meta_test_details, smallSize, C.muted, F.regular, smallLineHeight, bodyLayer)
        end
        if state.meta_test_data_stale == true then
            drawWrappedValue("TEST DATA CHANGED — refresh before the next play.", smallSize,
                C.warning or C.accent, F.medium, smallLineHeight, (layer or 100) + 6)
        end
        if not adbEnabled then
            drawWrappedValue("Enable Local beatmap testing in your ReSONANCE account settings.",
                smallSize, C.text, F.medium, smallLineHeight, bodyLayer)
            drawWrappedValue("Keep the phone and PC on the same private Wi-Fi; allow TCP/UDP 8765 in Windows Firewall.",
                smallSize, C.muted, F.regular, smallLineHeight, bodyLayer)
        end

        -- Keep actions docked to the bottom of the panel; text above can wrap freely.
        if state.meta_test_running == true then
            local gap = 8 * scale
            local half = (panelW - padding * 2 - gap) / 2
            UI.draw_button({ id = "editor_lan_test_refresh", x = x + padding, y = testButtonY,
                width = half, height = 38 * scale,
                label = state.meta_test_busy and "REFRESHING..." or "REFRESH TEST DATA",
                action = "editor.lan_test.refresh", enabled = state.meta_test_refresh_enabled == true,
                variant = "secondary", font = F.medium, text_size = fontSize("small"), layer = (layer or 100) + 7 })
            UI.draw_button({ id = "editor_lan_test_stop", x = x + padding + half + gap, y = testButtonY,
                width = half, height = 38 * scale, label = "STOP", action = "editor.lan_test.stop",
                enabled = state.meta_test_stop_enabled == true, variant = "danger", font = F.medium,
                text_size = fontSize("small"), layer = (layer or 100) + 7 })
        else
            UI.draw_button({ id = "editor_lan_test_start", x = x + padding, y = testButtonY,
                width = math.max(1, panelW - padding * 2), height = 38 * scale,
                label = state.meta_test_busy and "PREPARING..." or "START LOCAL TEST",
                action = "editor.lan_test.start", enabled = state.meta_test_start_enabled == true,
                variant = "success", font = F.medium, text_size = fontSize("body"), layer = (layer or 100) + 7 })
        end
    elseif state.meta_category == "MEDIA" then
        -- Keep music and preview controls at the top and give album artwork its own
        -- square-card area, matching the C# fallback Info panel. The native renderer
        -- draws the loaded textures over these frames without cropping them.
        if rows[1] then drawMetaRow(rows[1], contentTop) end
        if rows[2] then drawMetaRow(rows[2], contentTop + rowPitch) end

        local album = rows[3]
        local albumBig = rows[4]
        local hasBigAlbum = albumBig ~= nil
        local cardGap = hasBigAlbum and (16 * scale) or 0
        local innerWidth = math.max(1, panelW - padding * 2)
        local cardWidth = hasBigAlbum and math.max(1, (innerWidth - cardGap) / 2) or innerWidth
        local cardTop = contentTop + 2 * rowPitch
        local cardHeight = math.max(1, contentBottom - cardTop)

        local function drawAlbumCard(item, cardX, cardW, buttonId, action)
            if not item then return end
            label(item.label, cardX, cardTop, fontSize("small"), C.muted, nil, (layer or 100) + 4)
            local frameSize = math.max(1, math.min(220 * scale, cardW - 20 * scale,
                cardHeight - 92 * scale))
            local frameX = cardX + (cardW - frameSize) * 0.5
            local frameY = cardTop + 34 * scale
            -- Keep the artwork frame ID distinct from UI.draw_button's generated
            -- `<button id>_frame` ID for the SET/CHANGE IMAGE button below.
            draw.rect({ id = buttonId .. "_artwork_frame", x = frameX, y = frameY,
                width = frameSize, height = frameSize, color = C.button, layer = (layer or 100) + 5 })
            local edge = math.max(1, 2 * scale)
            draw.rect({ x = frameX, y = frameY, width = frameSize, height = edge,
                color = C.accent, layer = (layer or 100) + 6 })
            draw.rect({ x = frameX, y = frameY + frameSize - edge, width = frameSize, height = edge,
                color = C.accent, layer = (layer or 100) + 6 })
            draw.rect({ x = frameX, y = frameY, width = edge, height = frameSize,
                color = C.accent, layer = (layer or 100) + 6 })
            draw.rect({ x = frameX + frameSize - edge, y = frameY, width = edge, height = frameSize,
                color = C.accent, layer = (layer or 100) + 6 })
            ui.button({ id = buttonId .. "_frame_input", x = frameX, y = frameY,
                width = frameSize, height = frameSize, action = action })
            UI.draw_button({ id = buttonId, x = cardX + 8 * scale,
                y = frameY + frameSize + 8 * scale, width = math.max(1, cardW - 16 * scale),
                height = 34 * scale,
                label = tostring(item.value or ""):find("change", 1, true) and "CHANGE IMAGE" or "SET IMAGE",
                action = action, font = F.medium, text_size = fontSize("small"),
                layer = (layer or 100) + 7 })
        end

        if hasBigAlbum then
            drawAlbumCard(album, x + padding, cardWidth, "editor_meta_album", "editor.meta.media_album")
            drawAlbumCard(albumBig, x + padding + cardWidth + cardGap, cardWidth,
                "editor_meta_album_big", "editor.meta.media_album_big")
        else
            drawAlbumCard(album, x + padding, innerWidth, "editor_meta_album", "editor.meta.media_album")
        end
    else
        local rowY = contentTop
        for index = rowStart + 1, #rows do
            if rowY >= contentBottom then break end
            local item = rows[index]
            local rowLayout = dynamicLayouts[index]
            if index > rowStart + 1 and rowY + rowLayout.pitch > contentBottom then break end
            drawMetaRow(item, rowY, rowLayout)
            rowY = rowY + rowLayout.pitch
        end

        if maxScrollStart > 0 and contentHeight > 0 then
            local barX = x + panelW - padding - 10 * scale
            local barY = contentTop
            local barH = math.max(1, contentHeight)
            local thumbH = math.max(30 * scale, barH * contentHeight / math.max(1, totalRowsHeight))
            thumbH = math.min(barH, thumbH)
            local thumbTravel = math.max(0, barH - thumbH)
            local currentScrollHeight = metaPrefixHeight(dynamicLayouts, rowStart)
            local maxScrollHeight = math.max(1, metaPrefixHeight(dynamicLayouts, maxScrollStart))
            local thumbY = barY + thumbTravel * clamp(currentScrollHeight / maxScrollHeight, 0, 1)
            draw.rect({ x = barX + 4 * scale, y = barY, width = 5 * scale, height = barH,
                color = C.button, layer = (layer or 100) + 6 })
            draw.rect({ x = barX + 4 * scale, y = thumbY, width = 5 * scale, height = thumbH,
                color = C.accent, layer = (layer or 100) + 7 })
            ui.slider({ id = "editor_meta_scrollbar", x = barX, y = barY,
                width = 14 * scale, height = barH, min = 0, max = maxScrollStart, step = 1,
                axis = "vertical", action = "editor.meta.scroll_to" })
        end
    end

    if state.meta_category ~= "TEST" then
    local difficultyToggleLabel = tostring(state.meta_difficulty_label or "DIFFICULTY") ..
        (state.meta_difficulty_enabled and "  ENABLED" or "  DISABLED")
    if state.meta_difficulty_has_chart == false then
        draw.rect({ id = "editor_meta_difficulty_toggle_disabled", x = x + padding, y = difficultyToggleY,
            width = math.max(1, panelW - padding * 2), height = difficultyToggleH,
            color = C.button, alpha = 0.75, layer = (layer or 100) + 6 })
        draw.text(difficultyToggleLabel, { x = x + panelW * 0.5, y = difficultyToggleY + difficultyToggleH * 0.5,
            size = fontSize("small"), color = C.muted, font = F.regular, anchor = "center",
            layer = (layer or 100) + 7 })
    else
        UI.draw_button({ id = "editor_meta_difficulty_toggle", x = x + padding, y = difficultyToggleY,
            width = math.max(1, panelW - padding * 2), height = difficultyToggleH,
            label = difficultyToggleLabel, action = "editor.meta.difficulty_toggle",
            variant = state.meta_difficulty_enabled and "success" or "danger",
            font = F.medium, text_size = fontSize("small"), layer = (layer or 100) + 6 })
    end
    UI.draw_button({ id = "editor_meta_export_toggle", x = x + padding, y = exportToggleY,
        width = math.max(1, panelW - padding * 2), height = exportToggleH, label = "EXPORT",
        action = "editor.meta.export_toggle", font = F.medium, text_size = fontSize("body"),
        layer = (layer or 100) + 6 })
    end

    -- Register the element options after the rows, scrollbar, and export controls.
    -- Expanded options can overlap those controls, and the UI dispatcher gives
    -- the last registered overlapping control priority.
    if state.meta_element_open == true then
        local rows = state.meta_rows or {}
        local rowStart = clamp(num(state.meta_scroll, 0), 0, maxScrollStart)
        for index = rowStart + 1, #rows do
            local item = rows[index]
            local rowY = contentTop + metaPrefixHeight(dynamicLayouts, index - 1) - metaPrefixHeight(dynamicLayouts, rowStart)
            if rowY >= contentBottom then break end
            if item and item.label == "ELEMENT" then
                local rowLayout = dynamicLayouts[index]
                local fieldY = rowY + rowLabelH + rowLabelGap
                local fieldX = x + padding
                local fieldW = rowLayout.fieldW
                local fieldH = rowLayout.fieldH
                local options = state.meta_element_options or {}
                local elementGap = 4 * scale
                for optionIndex, option in ipairs(options) do
                    button(option, fieldX,
                        fieldY + fieldH + 2 * scale + (optionIndex - 1) * (28 * scale + elementGap),
                        fieldW, 28 * scale,
                        { layer = (layer or 100) + 12, text_size = fontSize("small"), hit_test_priority = 50,
                          variant = "secondary", selected_visual = false, background_color = C.button,
                          accent_color = option.element_color, accent_hover_color = option.element_hover_color })
                end
                break
            end
        end
    end
    drawPreviewEditor(state, w, h, (layer or 100) + 20)
end


local function drawDeleteDifficultyPopup(state, w, h, layer)
    if state.meta_delete_confirm_open ~= true then return end
    local scale = ACTIVE_SCALE
    local panelW = math.min(520 * scale, w - 48 * scale)
    local panelH = 220 * scale
    local x, y = (w - panelW) * 0.5, (h - panelH) * 0.5
    local base = layer or 170
    ui.button({ id = "editor_delete_difficulty_backdrop", x = 0, y = 0, width = w, height = h,
        action = "editor.meta.delete_cancel", hit_test_priority = 180 })
    draw.rect({ x = 0, y = 0, width = w, height = h,
        color = { r = 0, g = 0, b = 0, a = 190 }, layer = base })
    UI.draw_panel({ id = "editor_delete_difficulty_panel", x = x, y = y, width = panelW, height = panelH,
        color = C.panel, alpha = 0.99, layer = base + 1 })
    draw.text("DELETE DIFFICULTY?", { parent = "editor_delete_difficulty_panel", x = panelW * 0.5,
        y = 28 * scale, size = fontSize("heading"), color = C.text, font = F.medium,
        anchor = "center", layer = base + 3 })
    local bodyLines = wrapMeasuredText("This marks the chart for deletion. Its file will be removed when you save.",
        panelW - 36 * scale, fontSize("description"), F.regular)
    for index, line in ipairs(bodyLines) do
        draw.text(line, { parent = "editor_delete_difficulty_panel", x = panelW * 0.5,
            y = (69 + (index - 1) * 21) * scale, size = fontSize("description"), color = C.muted,
            font = F.regular, anchor = "center", layer = base + 3 })
    end
    local gap = 12 * scale
    local bw = (panelW - 56 * scale - gap) / 2
    local buttonH = 42 * scale
    local by = y + panelH - 62 * scale
    button({ id = "editor_delete_difficulty_cancel", label = "CANCEL", action = "editor.meta.delete_cancel" },
        x + 22 * scale, by, bw, buttonH, { layer = base + 4, hit_test_priority = 190 })
    button({ id = "editor_delete_difficulty_yes", label = "DELETE", action = "editor.meta.delete_yes", variant = "danger" },
        x + 22 * scale + bw + gap, by, bw, buttonH, { layer = base + 4, hit_test_priority = 190 })
end

local function drawCreateDifficultyPopup(state, w, h, layer)
    if state.meta_create_confirm_open ~= true then return end
    local scale = ACTIVE_SCALE
    local panelW = math.min(520 * scale, w - 48 * scale)
    local panelH = 220 * scale
    local x, y = (w - panelW) * 0.5, (h - panelH) * 0.5
    local base = layer or 180
    local difficulty = tostring(state.meta_create_difficulty_label or "difficulty")
    ui.button({ id = "editor_create_difficulty_backdrop", x = 0, y = 0, width = w, height = h,
        action = "editor.meta.create_cancel", hit_test_priority = 180 })
    draw.rect({ x = 0, y = 0, width = w, height = h,
        color = { r = 0, g = 0, b = 0, a = 190 }, layer = base })
    UI.draw_panel({ id = "editor_create_difficulty_panel", x = x, y = y, width = panelW, height = panelH,
        color = C.panel, alpha = 0.99, layer = base + 1 })
    draw.text("CREATE DIFFICULTY?", { parent = "editor_create_difficulty_panel", x = panelW * 0.5,
        y = 28 * scale, size = fontSize("heading"), color = C.text, font = F.medium,
        anchor = "center", layer = base + 3 })
    local bodyLines = wrapMeasuredText("Create an empty " .. difficulty .. " chart file now? You can add notes afterward.",
        panelW - 36 * scale, fontSize("description"), F.regular)
    for index, line in ipairs(bodyLines) do
        draw.text(line, { parent = "editor_create_difficulty_panel", x = panelW * 0.5,
            y = (69 + (index - 1) * 21) * scale, size = fontSize("description"), color = C.muted,
            font = F.regular, anchor = "center", layer = base + 3 })
    end
    local gap = 12 * scale
    local bw = (panelW - 56 * scale - gap) / 2
    local buttonH = 42 * scale
    local by = y + panelH - 62 * scale
    button({ id = "editor_create_difficulty_cancel", label = "CANCEL", action = "editor.meta.create_cancel" },
        x + 22 * scale, by, bw, buttonH, { layer = base + 4, hit_test_priority = 190 })
    button({ id = "editor_create_difficulty_yes", label = "CREATE", action = "editor.meta.create_yes", variant = "success" },
        x + 22 * scale + bw + gap, by, bw, buttonH, { layer = base + 4, hit_test_priority = 190 })
end

function drawPreviewEditor(state, w, h, layer)
    if state.preview_editor_open ~= true then return end
    local scale = math.max(0.55, math.min(w / 1920, h / 1080))
    local margin = 18 * scale
    local panelW = math.min(w - margin * 2, LAYOUT.preview_width * scale)
    local panelH = math.min(h - margin * 2, LAYOUT.preview_height * scale)
    local x = (w - panelW) * 0.5
    local y = (h - panelH) * 0.5
    ui.button({ id = "editor_preview_backdrop", x = 0, y = 0, width = w, height = h,
        action = "editor.block" })
    draw.rect({ x = 0, y = 0, width = w, height = h,
        color = { r = 0, g = 0, b = 0, a = 185 }, layer = layer or 130 })
    panel("editor_preview_panel", x, y, panelW, panelH, "MUSIC PREVIEW", layer or 130)

    local duration = math.max(0, num(state.preview_duration, 0))
    local subtitle = duration > 0
        and ("Choose a section of the full song. Length: " .. tostring(state.preview_duration_label) .. ". A subtle fade is added at each end.")
        or "Import the full song audio first to enable preview editing."
    label(subtitle, x + 28 * scale, y + 76 * scale, fontSize("description"), C.muted, nil, (layer or 130) + 3)

    local side = LAYOUT.preview_side_margin * scale
    local sliderX = x + side
    local sliderY = y + LAYOUT.preview_slider_y * scale
    local sliderW = math.max(1, panelW - side * 2)
    local startValue = clamp(num(state.preview_start, 0), 0, math.max(0.001, duration))
    local endValue = clamp(num(state.preview_end, duration), startValue, math.max(startValue, duration))
    local startRatio = duration > 0 and startValue / duration or 0
    local endRatio = duration > 0 and endValue / duration or 0
    draw.rect({ x = sliderX, y = sliderY, width = sliderW, height = 6 * scale,
        color = C.hover, layer = (layer or 130) + 3 })
    if endRatio > startRatio then
        draw.rect({ x = sliderX + sliderW * startRatio, y = sliderY - 2 * scale,
            width = sliderW * (endRatio - startRatio), height = 10 * scale,
            color = C.accent, layer = (layer or 130) + 4 })
    end
    local thumbW = 14 * scale
    draw.rect({ x = sliderX + sliderW * startRatio - thumbW / 2, y = sliderY - 8 * scale,
        width = thumbW, height = 22 * scale, color = C.text, layer = (layer or 130) + 5 })
    draw.rect({ x = sliderX + sliderW * endRatio - thumbW / 2, y = sliderY - 8 * scale,
        width = thumbW, height = 22 * scale, color = C.primary, layer = (layer or 130) + 5 })
    ui.slider({ id = "editor_preview_range", x = sliderX, y = sliderY - 10 * scale,
        width = sliderW, height = 26 * scale, min = 0, max = math.max(0.001, duration),
        action = "editor.preview.scrub" })
    label("00:00.000", sliderX, sliderY + 24 * scale, fontSize("small"), C.muted, nil, (layer or 130) + 3)
    label(state.preview_duration_label or "00:00.000", sliderX + sliderW, sliderY + 24 * scale,
        fontSize("small"), C.muted, nil, (layer or 130) + 3)

    local inputY = y + LAYOUT.preview_input_y * scale
    local inputGap = 24 * scale
    local inputW = (panelW - side * 2 - inputGap) / 2
    label("START TIME (SECONDS)", sliderX, inputY - 30 * scale, fontSize("small"), C.muted, nil, (layer or 130) + 3)
    label("END TIME (SECONDS)", sliderX + inputW + inputGap, inputY - 30 * scale,
        fontSize("small"), C.muted, nil, (layer or 130) + 3)
    drawTextField("editor_preview_start_input", state.preview_start_text or "0.000",
        "editor.preview.focus_start", nil, sliderX, inputY, inputW, 44 * scale,
        (layer or 130) + 5, state.preview_start_focused == true, true,
        state.preview_start_caret_offset, state.preview_start_selection_offset,
        state.preview_start_selection_width, state.preview_start_text_width)
    drawTextField("editor_preview_end_input", state.preview_end_text or "0.000",
        "editor.preview.focus_end", nil, sliderX + inputW + inputGap, inputY, inputW, 44 * scale,
        (layer or 130) + 5, state.preview_end_focused == true, true,
        state.preview_end_caret_offset, state.preview_end_selection_offset,
        state.preview_end_selection_width, state.preview_end_text_width)

    if state.preview_message ~= nil and state.preview_message ~= "" then
        label(state.preview_message, x + 28 * scale, inputY + 58 * scale,
            fontSize("body"), C.text, nil, (layer or 130) + 4)
    end

    local buttonGap = 10 * scale
    local buttonY = y + panelH - 60 * scale
    local buttonPad = 22 * scale
    local buttonW = (panelW - buttonPad * 2 - buttonGap * 2) / 3
    UI.draw_button({ id = "editor_preview_play", x = x + buttonPad, y = buttonY,
        width = buttonW, height = 38 * scale, label = state.preview_playing and "STOP" or "PLAY",
        action = "editor.preview.play", font = F.medium, text_size = fontSize("body"),
        layer = (layer or 130) + 5 })
    UI.draw_button({ id = "editor_preview_save", x = x + buttonPad + buttonW + buttonGap, y = buttonY,
        width = buttonW, height = 38 * scale, label = "SAVE", action = "editor.preview.save",
        font = F.medium, text_size = fontSize("body"), layer = (layer or 130) + 5 })
    UI.draw_button({ id = "editor_preview_close", x = x + buttonPad + (buttonW + buttonGap) * 2, y = buttonY,
        width = buttonW, height = 38 * scale, label = "CLOSE", action = "editor.preview.close",
        font = F.medium, text_size = fontSize("body"), layer = (layer or 130) + 5 })
end

local function drawInfo(state, x, y, width, problemsY, layer, screenWidth)
    if state.hud_visible ~= true then return end
    local lineGap = 30 * ACTIVE_SCALE
    local infoLines = {
        { state.hud_line, C.text },
        { state.selection_line, state.selection_active and C.accent or C.muted },
        { state.play_status, state.playing and C.accent or C.muted },
    }
    for i, item in ipairs(infoLines) do
        if item[1] and item[1] ~= "" then
            draw.text(fittedText(item[1], width, fontSize("body")), {
                x = x, y = y + (i - 1) * lineGap, size = fontSize("body"),
                color = item[2], font = F.regular, anchor = "top-left", layer = layer,
            })
        end
    end

    local debugLines = state.score_debug_lines or {}
    local debugY = y + #infoLines * lineGap + 4 * ACTIVE_SCALE
    for i, line in ipairs(debugLines) do
        draw.text(fittedText(line, width, fontSize("small")), {
            x = x, y = debugY + (i - 1) * 26 * ACTIVE_SCALE, size = fontSize("small"),
            color = i == 4 and C.text or C.muted, font = F.regular,
            anchor = "top-left", layer = layer,
        })
    end
    local superLines = state.super_flick_debug_lines or {}
    local superY = debugY + #debugLines * 26 * ACTIVE_SCALE
    for i, line in ipairs(superLines) do
        draw.text(fittedText(line, width, fontSize("small")), {
            x = x, y = superY + (i - 1) * 26 * ACTIVE_SCALE, size = fontSize("small"),
            color = i == 1 and C.accent or C.muted, font = F.regular,
            anchor = "top-left", layer = layer,
        })
    end

    local bottomLines = {}
    if state.source_name and state.source_name ~= "" then
        table.insert(bottomLines, { text = state.source_name, color = C.muted })
    end
    if state.status_message and state.status_message ~= "" then
        table.insert(bottomLines, { text = state.status_message, color = C.text })
    end
    if #bottomLines == 0 then return end

    -- Status/source info is not part of the Problems panel: give it the remaining
    -- screen width, then wrap it into as many lines as needed instead of ellipsizing.
    local rightMargin = LAYOUT.margin * ACTIVE_SCALE
    local bottomWidth = math.max(width, (tonumber(screenWidth) or (x + width)) - x - rightMargin)
    local textSize = fontSize("small")
    local wrappedGroups = {}
    local totalHeight = 0
    local lineHeight = math.max(20 * ACTIVE_SCALE, textSize * 1.4)
    for _, item in ipairs(bottomLines) do
        local lines = wrapMeasuredText(item.text, bottomWidth, textSize, F.regular)
        table.insert(wrappedGroups, { lines = lines, color = item.color })
        totalHeight = totalHeight + #lines * lineHeight
    end
    if #wrappedGroups > 1 then totalHeight = totalHeight + (#wrappedGroups - 1) * 4 * ACTIVE_SCALE end

    local bottomY = problemsY - totalHeight - 8 * ACTIVE_SCALE
    for groupIndex, group in ipairs(wrappedGroups) do
        for _, line in ipairs(group.lines) do
            draw.text(line, {
                x = x, y = bottomY, size = textSize, color = group.color,
                font = F.regular, anchor = "top-left", layer = layer,
            })
            bottomY = bottomY + lineHeight
        end
        if groupIndex < #wrappedGroups then bottomY = bottomY + 4 * ACTIVE_SCALE end
    end
end

local function drawAuxDebug(state, w, margin, actionX, scale)
    if state.aux_debug_visible ~= true then return end
    local lines = state.aux_debug_lines or {}
    local panelW = math.min(620 * scale, w - margin * 2)
    local x = math.max(margin, actionX - panelW - 12 * scale)
    local y = 116 * scale
    local rowHeight = 28 * scale
    local contentRows = 2 + math.max(1, #lines)
    local panelH = 24 * scale + contentRows * rowHeight + 12 * scale
    UI.draw_panel({ id = "editor_aux_debug", x = x, y = y,
        width = panelW, height = panelH, color = C.panel, alpha = 0.97, layer = 30 })
    draw.text(fittedText(state.aux_debug_title, panelW - 24 * scale, fontSize("body")), {
        x = x + 12 * scale, y = y + 8 * scale,
        size = fontSize("body"), color = C.accent, font = F.medium,
        anchor = "top-left", layer = 32,
    })
    draw.text(fittedText(state.aux_debug_cursor, panelW - 24 * scale, fontSize("small")), {
        x = x + 12 * scale, y = y + 36 * scale,
        size = fontSize("small"), color = C.muted, font = F.regular,
        anchor = "top-left", layer = 32,
    })
    if state.aux_debug_empty == true then
        draw.text("<no future auxiliary events>", {
            x = x + 12 * scale, y = y + 64 * scale,
            size = fontSize("small"), color = C.muted, font = F.regular,
            anchor = "top-left", layer = 32,
        })
    else
        for i, item in ipairs(lines) do
            draw.text(fittedText(item.label, panelW - 24 * scale, fontSize("small")), {
                x = x + 12 * scale, y = y + (64 + (i - 1) * 28) * scale,
                size = fontSize("small"), color = item.active and C.text or C.muted,
                font = F.regular, anchor = "top-left", layer = 32,
            })
        end
    end
end

local function drawExportPopup(state, w, h, layer)
    if state.meta_export_expanded ~= true then return end
    local scale = ACTIVE_SCALE
    local options = state.meta_export_options or {}
    if #options == 0 then return end
    local margin = 16 * scale
    local sidePadding = 18 * scale
    local rowGap = 6 * scale
    local headerArea = 112 * scale
    local bottomPadding = 18 * scale
    local panelW = math.min(760 * scale, math.max(1, w - margin * 2))
    local optionW = math.max(1, panelW - sidePadding * 2)
    local textWidth = math.max(1, optionW - 32 * scale)
    local titleSize = fontSize("body")
    local descSize = fontSize("small")
    local titleLineH = titleSize * 1.18
    local descLineH = descSize * 1.28
    local titleTop = 10 * scale
    local descGap = 4 * scale
    local rowBottom = 10 * scale
    local rows = {}
    local rowsHeight = 0

    for i, item in ipairs(options) do
        local lines = wrapMeasuredText(item.description or "", textWidth, descSize, F.regular)
        local rowH = titleTop + titleLineH + descGap + #lines * descLineH + rowBottom
        rows[i] = { item = item, lines = lines, height = rowH }
        rowsHeight = rowsHeight + rowH
    end

    local requestedH = headerArea + rowsHeight + rowGap * math.max(0, #rows - 1) + bottomPadding
    local panelH = math.min(requestedH, math.max(1, h - margin * 2))
    local x = (w - panelW) * 0.5
    local y = (h - panelH) * 0.5
    local contentTop = y + headerArea
    local baseLayer = layer or 150

    -- Register a full-screen hit target first so clicks outside an option close the popup.
    ui.button({ id = "editor_export_backdrop", x = 0, y = 0, width = w, height = h,
        action = "editor.meta.export_close", hit_test_priority = 90 })
    draw.rect({ x = 0, y = 0, width = w, height = h,
        color = { r = 0, g = 0, b = 0, a = 190 }, layer = baseLayer })
    UI.draw_panel({ id = "editor_export_panel", x = x, y = y, width = panelW, height = panelH,
        color = C.panel, alpha = 0.99, layer = baseLayer + 1 })
    draw.rect({ parent = "editor_export_panel", x = 0, y = 0, width = panelW, height = 4 * scale,
        color = C.accent, layer = baseLayer + 2 })
    draw.text("EXPORT CHART", { parent = "editor_export_panel", x = panelW * 0.5,
        y = 32 * scale, size = fontSize("heading"), color = C.text, font = F.medium,
        anchor = "center", layer = baseLayer + 3 })
    draw.text("Choose a format for this chart or song bundle.", { parent = "editor_export_panel",
        x = panelW * 0.5, y = 68 * scale, size = fontSize("small"), color = C.muted,
        font = F.regular, anchor = "center", layer = baseLayer + 3 })

    local rowY = contentTop
    for _, row in ipairs(rows) do
        local item = row.item
        local rowH = row.height
        UI.draw_button({ id = item.id, x = x + sidePadding, y = rowY,
            width = optionW, height = rowH, label = "", action = item.action, value = item.value,
            selected = item.selected == true, variant = item.variant or "secondary", accent = true,
            font = F.medium, text_size = titleSize, hit_test_priority = 110,
            layer = baseLayer + 4 })
        draw.text(item.label or "", {
            parent = item.id .. "_frame", x = 16 * scale, y = titleTop,
            size = titleSize, color = C.text, font = F.medium,
            anchor = "top-left", layer = baseLayer + 6,
        })
        for lineIndex, line in ipairs(row.lines) do
            draw.text(line, {
                parent = item.id .. "_frame", x = 16 * scale,
                y = titleTop + titleLineH + descGap + (lineIndex - 1) * descLineH,
                size = descSize, color = C.muted, font = F.regular,
                anchor = "top-left", layer = baseLayer + 6,
            })
        end
        rowY = rowY + rowH + rowGap
    end
end

function init() end

function render()
    local s = chart_editor
    local w = math.max(1, screen.width)
    local h = math.max(1, screen.height)
    local scale = math.max(0.55, math.min(w / 1920, h / 1080))
    ACTIVE_SCALE = scale
    local margin = LAYOUT.margin * scale
    local gap = LAYOUT.gap * scale
    local toolbarW = math.min(LAYOUT.toolbar_rail_width * scale, w * 0.28)
    if s.panels_view == true then
        toolbarW = math.min(LAYOUT.panels_toolbar_width * scale, w * 0.28)
    end
    local dockX = w - margin - toolbarW

    local bottomActions = s.actions_left or {}
    local leftPadding = LAYOUT.bottom_action_padding * scale
    local leftContentW = math.max(1, w - margin * 2 - leftPadding * 2)
    local leftColumns = math.max(1, #bottomActions)
    if s.preview_mode == true then
        local playAction, previewAction, gameplayAction
        for _, item in ipairs(bottomActions) do
            local value = tonumber(item.value)
            if value == 0 then playAction = item end
            if value == 21 then previewAction = item end
            if value == 22 then gameplayAction = item end
        end
        local buttonGap = LAYOUT.bottom_action_gap * scale
        local buttonH = LAYOUT.bottom_action_height * scale
        -- Keep the three preview controls at the same width they use in the
        -- normal toolbar, but size the background to only this compact group.
        local buttonW = (leftContentW - buttonGap * (leftColumns - 1)) / leftColumns
        local stripW = leftPadding * 2 + buttonW * 3 + buttonGap * 2
        local stripH = leftPadding * 2 + buttonH
        local stripX = margin
        local stripY = h - margin - stripH
        ui.button({ id = "editor_bottom_left_panel", x = stripX, y = stripY,
            width = stripW, height = stripH, action = "editor.block" })
        draw.rect({ x = stripX, y = stripY, width = stripW, height = stripH,
            color = { r = 9, g = 9, b = 10, a = 224 }, layer = 10 })
        drawBottomActionButton(playAction, stripX + leftPadding, stripY + leftPadding,
            buttonW, buttonH, 12)
        drawBottomActionButton(previewAction, stripX + leftPadding + buttonW + buttonGap, stripY + leftPadding,
            buttonW, buttonH, 12)
        drawBottomActionButton(gameplayAction, stripX + leftPadding + (buttonW + buttonGap) * 2, stripY + leftPadding,
            buttonW, buttonH, 12)
        return
    end
    local leftPanelW = w - margin * 2
    local leftPanelH = LAYOUT.bottom_action_height * scale + leftPadding * 2
    local leftPanelY = h - margin - leftPanelH
    local rightStripH = s.panels_view == true
        and (16 + 32 * 3 + 4 * 2) * scale
        or (8 * 2 + 34 * 4 + 5 * 3) * scale
    local rightStripY = leftPanelY - LAYOUT.panel_gap * scale - rightStripH
    local actionWidth = math.min(LAYOUT.action_column_width * scale, w * 0.28)
    local actionX = w - margin - actionWidth

    -- A single full-width bottom strip holds every editor action in one row.
    ui.button({ id = "editor_bottom_left_panel", x = margin, y = leftPanelY,
        width = leftPanelW, height = leftPanelH, action = "editor.block" })
    draw.rect({ x = margin, y = leftPanelY, width = leftPanelW, height = leftPanelH,
        color = { r = 9, g = 9, b = 10, a = 224 }, layer = 10 })
    drawBottomActions(bottomActions, margin + leftPadding, leftPanelY + leftPadding,
        leftContentW, 12, leftColumns)

    -- Edit actions remain a single vertical column in the top-right corner.
    drawActionRail(s.actions_right, actionX, margin, actionWidth, 1)

    -- Scroll speed stays at the top-left, using the declared UI text sizes.
    local speedW = LAYOUT.scroll_speed_width * scale
    local speedH = 50 * scale
    panel("editor_scroll_speed", margin, margin, speedW, speedH, nil, 4)
    UI.draw_button({ id = "editor_speed_down", x = margin + 6 * scale, y = margin + 8 * scale,
        width = 34 * scale, height = 34 * scale, label = "−", action = "editor.speed", value = -1,
        font = F.medium, text_size = fontSize("body"), layer = 6 })
    label(s.scroll_speed, margin + 46 * scale, margin + 16 * scale,
        fontSize("body"), C.muted, nil, 6)
    UI.draw_button({ id = "editor_speed_up", x = margin + speedW - 40 * scale, y = margin + 8 * scale,
        width = 34 * scale, height = 34 * scale, label = "+", action = "editor.speed", value = 1,
        font = F.medium, text_size = fontSize("body"), layer = 6 })

    -- Keep the vertical timeline at the left edge. Lua draws its labels and the
    -- native slider forwards seek values to the existing C# playback code.
    local timelineX = margin
    local timelineY = margin + speedH + 10 * scale
    local timelineW = LAYOUT.timeline_width * scale
    local timelineH = math.max(220 * scale, leftPanelY - timelineY - 10 * scale)
    drawTimeline(s, timelineX, timelineY, timelineW, timelineH, 6)

    -- The chart health panel sits above the full-width bottom strip and beside
    -- the timeline, leaving the right-side tool rail clear.
    local problemX = timelineX + timelineW + gap
    local problemW = math.max(240 * scale, math.min(900 * scale, actionX - problemX - gap))
    local problemCount = num(s.problems_count, #(s.problems or {}))
    local problemsHeight = s.problems_expanded == true
        and (104 + math.min(LAYOUT.problem_rows, problemCount) * 44) * scale
        or 52 * scale
    local problemY = math.max(margin + 70 * scale, leftPanelY - problemsHeight - 10 * scale)
    drawProblems(s, problemX, problemY, problemW, 2)
    drawInfo(s, problemX, margin + speedH + 10 * scale,
        math.max(100 * scale, actionX - problemX - gap), problemY, 3, w)
    drawAuxDebug(s, w, margin, actionX, scale)

    -- Bank indicators use the same projected note-lane positions as the editor.
    drawBankButtons(s.banks, 0, 0, 0, 3)

    -- The right category rail sits above the full-width bottom row; expanded
    -- menus open to its left and align with their owning category.
    drawToolbar(s, w, dockX, rightStripY, toolbarW, 4)
    drawMeta(s, w, h, 100)

    -- Register open snap/metronome choices last so overlapping toolbar or info
    -- controls cannot steal clicks. Anchor each option list to its own bottom-row
    -- button, whose width and position match the rendered control.
    if s.preview_editor_open ~= true and (s.section == "snap" or s.section == "metronome") then
        local options = s.section == "snap" and s.snap_options or s.metronome_options
        local targetActionValue = s.section == "snap" and 16 or 20
        local buttonGap = LAYOUT.bottom_action_gap * scale
        local buttonH = LAYOUT.bottom_action_height * scale
        local buttonW = (leftContentW - buttonGap * (leftColumns - 1)) / leftColumns
        local slots = bottomActionSlots(bottomActions, leftColumns)
        local anchorX, anchorY
        for _, slot in ipairs(slots) do
            if tonumber(slot.item.value) == targetActionValue then
                anchorX = margin + leftPadding + slot.col * (buttonW + buttonGap)
                anchorY = leftPanelY + leftPadding + slot.row * (buttonH + buttonGap)
                break
            end
        end
        if anchorX ~= nil and anchorY ~= nil then
            local boxW = buttonW
            local rowH = 42 * scale
            local boxH = #options * rowH + 8 * scale
            local x = anchorX
            local y = anchorY - boxH - 4 * scale
            ui.button({ id = "editor_popup_shield", x = x, y = y, width = boxW, height = boxH,
                action = "editor.block", hit_test_priority = 50 })
            draw.rect({ x = x, y = y, width = boxW, height = boxH, color = C.panel,
                alpha = 0.97, layer = 120 })
            for i, item in ipairs(options) do
                button(item, x, y + 4 * scale + (i - 1) * rowH,
                    boxW, rowH - 4 * scale,
                    { layer = 122, text_size = fontSize("body"), hit_test_priority = 60 })
            end
        end
    end

    drawExportPopup(s, w, h, 150)
    drawDeleteDifficultyPopup(s, w, h, 170)
    drawCreateDifficultyPopup(s, w, h, 180)
end

-- Retained only for the built-in C# emergency renderer. Normal chart editor
-- rendering is the Lua render() above; these values are never used to position
-- its controls.
return {
    layout = {
        edge_margin = 16, panel_padding = 5, panel_gap = 10,
        action_column_width = 166, action_button_height = 44, action_button_gap = 4,
        toolbar_width = 390, panels_toolbar_width = 200, left_toolbar_max_width = 960,
        left_toolbar_button_height = 40, left_toolbar_button_gap = 6,
        scroll_speed_width = 160, scroll_button_size = 34, action_button_gap = 7,
        metadata_panel_width = 660, metadata_panel_margin = 10,
        timeline_width = 126, problems_max_width = 900,
        problems_max_height = 300,
    },
    style = {
        button_font = F.medium, label_font = F.regular, header_font = F.regular,
        panel = C.panel, header = C.panel, button = C.button, hover = C.hover,
        pressed = C.pressed, selected_button = C.selected, text = C.text,
        muted_text = C.muted, accent = C.accent, danger = C.error,
    },
    labels = {},
}
