local opts = { seconds = 5, color = "FF0000" }
require("mp.options").read_options(opts, "protect-flash")
if opts.seconds <= 0 then return end

local overlay = mp.create_osd_overlay("ass-events")
local bgr = opts.color:sub(5, 6) .. opts.color:sub(3, 4) .. opts.color:sub(1, 2)
local started = mp.get_time()
local visible = false
local timer

local function rect(x1, y1, x2, y2)
    return string.format("m %d %d l %d %d %d %d %d %d ", x1, y1, x2, y1, x2, y2, x1, y2)
end

timer = mp.add_periodic_timer(0.3, function()
    if mp.get_time() - started > opts.seconds then
        timer:kill()
        overlay:remove()
        return
    end
    local w, h = mp.get_osd_size()
    if not w or w == 0 then return end
    visible = not visible
    if not visible then
        overlay:remove()
        return
    end
    local t = math.max(6, math.floor(math.min(w, h) / 30))
    overlay.res_x, overlay.res_y = w, h
    overlay.data = string.format("{\\an7\\pos(0,0)\\bord0\\shad0\\1c&H%s&\\p1}", bgr)
        .. rect(0, 0, w, t) .. rect(0, h - t, w, h)
        .. rect(0, t, t, h - t) .. rect(w - t, t, w, h - t) .. "{\\p0}"
    overlay:update()
end)
