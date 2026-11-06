-- UI theme override entry point. Custom skins may return a partial table; uiStyle.lua
-- merges it over ui/themeDefaults.lua so changing one color does not require
-- copying the entire theme. The Default skin simply exposes its base theme.
return import("ui/themeDefaults")
