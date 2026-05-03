-- DATA STRUCTURE:
-- Key: "ocr:model:{modelId}:workers" (Hash)
-- Field: {workerId}
-- Value: { "allowSlot": int, "used": int, "remainingPage": int, "TotalPage": int }
--
-- Input:
-- KEYS[1]: model_workers_key
-- ARGV[1]: workerId

local model_key = KEYS[1]
local worker_id = ARGV[1]

-- 1. Read global total_max
local total_max_raw = redis.call('HGET', model_key, '__config_total_max')
if not total_max_raw then return nil end
local total_max = tonumber(total_max_raw)

-- 2. Fetch current worker data
local raw = redis.call('HGET', model_key, worker_id)
if not raw then return nil end
local data = cjson.decode(raw)

-- 3. Local check: Does worker have slots in its quota?
if data.used >= data.allowSlot then
    return redis.error_reply("No slots available")
end

-- 4. Global check: Is the system-wide limit (N) reached?
local all_raw = redis.call('HGETALL', model_key)
local global_used = 0
for i = 1, #all_raw, 2 do
    local field = all_raw[i]
    if field ~= "__config_total_max" then
        local w_val = all_raw[i+1]
        local w_data = cjson.decode(w_val)
        global_used = global_used + (w_data.used or 0)
    end
end

if global_used >= total_max then
    return redis.error_reply("No slots available")
end


-- 5. Increment and save
data.used = data.used + 1
redis.call('HSET', model_key, worker_id, cjson.encode(data))

return cjson.encode(data)

